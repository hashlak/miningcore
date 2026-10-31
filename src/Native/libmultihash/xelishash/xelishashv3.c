/*
 * XelisHash v3 ("xel/v3") for hashlak/miningcore.
 *
 * Verbatim port of the official C implementation, xelis-project/xelis-hash C/xelis_hash_v3.c (MIT, commit f48262b,
 * 2025-11), with the test harness removed and three adaptations:
 *   - BLAKE3 and ChaCha20 come from libmultihash's own copies (../blake3, ../chacha20) through the same
 *     chacha_encrypt() wrapper that xelishashv2.cpp uses (state zeroed, key, 12-byte nonce, counter 0, 8 rounds);
 *   - the 531*128*8 = 543,744-byte scratchpad is a thread-local static, allocated once per thread;
 *   - the exported entry point takes (input, length, output) like the v1/v2 exports and refuses any length but 112.
 * Gold vectors (xelis-hash src/v3.rs tests): see xelishashv3_test.c. x86-64 only (divq/rorq/rolq, AES-NI, __uint128_t).
 */
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <wmmintrin.h>
#include "../blake3/blake3.h"
#include "../chacha20/chacha20.h"

#define INPUT_LEN (112)
#define MEMSIZE (531 * 128)
#define ITERS (2)
#define HASH_SIZE (32)

#define CHUNK_SIZE (32)
#define NONCE_SIZE (12)
#define OUTPUT_SIZE (MEMSIZE * 8)
#define CHUNKS (4)

#define KEY "xelishash-pow-v3"
#define BUFSIZE (MEMSIZE / 2)

static inline void blake3(const uint8_t *input, int len, uint8_t *output) {
	blake3_hasher hasher;
	blake3_hasher_init(&hasher);
	blake3_hasher_update(&hasher, input, len);
	blake3_hasher_finalize(&hasher, output, BLAKE3_OUT_LEN);
}

static void chacha_encrypt(uint8_t *key, uint8_t *nonce, uint8_t *in, uint8_t *out, size_t bytes, uint32_t rounds)
{
	uint8_t state[48] = {0};
	ChaCha20SetKey(state, key);
	ChaCha20SetNonce(state, nonce);
	ChaCha20EncryptBytes(state, in, out, bytes, rounds);
}

static void stage1(const uint8_t *input, uint8_t scratch_pad[OUTPUT_SIZE]) {
	uint8_t key[CHUNK_SIZE * CHUNKS] = {0};
	uint8_t input_hash[HASH_SIZE];
	uint8_t buffer[CHUNK_SIZE * 2];
	memcpy(key, input, INPUT_LEN);
	blake3(input, INPUT_LEN, buffer);

	uint8_t *t = scratch_pad;

	memcpy(buffer + CHUNK_SIZE, key + 0 * CHUNK_SIZE, CHUNK_SIZE);
	blake3(buffer, CHUNK_SIZE * 2, input_hash);
	chacha_encrypt(input_hash, buffer, NULL, t, OUTPUT_SIZE / CHUNKS, 8);

	t += OUTPUT_SIZE / CHUNKS;
	memcpy(buffer, input_hash, CHUNK_SIZE);
	memcpy(buffer + CHUNK_SIZE, key + 1 * CHUNK_SIZE, CHUNK_SIZE);
	blake3(buffer, CHUNK_SIZE * 2, input_hash);
	chacha_encrypt(input_hash, t - NONCE_SIZE, NULL, t, OUTPUT_SIZE / CHUNKS, 8);

	t += OUTPUT_SIZE / CHUNKS;
	memcpy(buffer, input_hash, CHUNK_SIZE);
	memcpy(buffer + CHUNK_SIZE, key + 2 * CHUNK_SIZE, CHUNK_SIZE);
	blake3(buffer, CHUNK_SIZE * 2, input_hash);
	chacha_encrypt(input_hash, t - NONCE_SIZE, NULL, t, OUTPUT_SIZE / CHUNKS, 8);

	t += OUTPUT_SIZE / CHUNKS;
	memcpy(buffer, input_hash, CHUNK_SIZE);
	memcpy(buffer + CHUNK_SIZE, key + 3 * CHUNK_SIZE, CHUNK_SIZE);
	blake3(buffer, CHUNK_SIZE * 2, input_hash);
	chacha_encrypt(input_hash, t - NONCE_SIZE, NULL, t, OUTPUT_SIZE / CHUNKS, 8);
}

// https://danlark.org/2020/06/14/128-bit-division
static inline uint64_t Divide128Div64To64(uint64_t high, uint64_t low, uint64_t divisor, uint64_t *remainder) {
	uint64_t result;
	__asm__("divq %[v]"
			: "=a"(result), "=d"(*remainder)
			: [v] "r"(divisor), "a"(low), "d"(high));
	return result;
}

static inline uint64_t udiv(uint64_t high, uint64_t low, uint64_t divisor) {
	uint64_t remainder;

	if (high < divisor) {
		return Divide128Div64To64(high, low, divisor, &remainder);
	}
	else {
		(void)Divide128Div64To64(0, high, divisor, &high);
		return Divide128Div64To64(high, low, divisor, &remainder);
	}
}

static inline uint64_t ROTR(uint64_t x, uint32_t r) {
	__asm__("rorq %%cl, %0" : "+r"(x) : "c"(r));
	return x;
}

static inline uint64_t ROTL(uint64_t x, uint32_t r) {
	__asm__("rolq %%cl, %0" : "+r"(x) : "c"(r));
	return x;
}

static inline __uint128_t combine_uint64(uint64_t high, uint64_t low) {
	return ((__uint128_t)high << 64) | low;
}

static inline uint64_t murmurhash3(uint64_t seed) {
	seed ^= seed >> 55;
	seed *= 0xff51afd7ed558ccdULL;
	seed ^= seed >> 32;
	seed *= 0xc4ceb9fe1a85ec53ULL;
	seed ^= seed >> 15;
	return seed;
}

static inline uint64_t map_index(uint64_t x) {
	x ^= x >> 33;
	x *= 0xff51afd7ed558ccdULL;
	return (uint64_t)(((__uint128_t)x * BUFSIZE) >> 64);
}

static inline int pick_half(uint64_t seed) {
	return (murmurhash3(seed) & (1ULL << 58)) != 0;
}

static uint64_t isqrt(uint64_t n) {
	if (n < 2)
		return n;

	// Compute the floating-point square root
	uint64_t approx = (uint64_t)sqrt((double)n);

	// Verify and adjust if necessary (unsigned arithmetic wraps exactly like the Rust wrapping_mul)
	if (approx * approx > n) {
		return approx - 1;
	} else if ((approx + 1) * (approx + 1) <= n) {
		return approx + 1;
	} else {
		return approx;
	}
}

static uint64_t modular_power(uint64_t base, uint64_t exp, uint64_t mod) {
	uint64_t result = 1;
	base %= mod;

	while (exp > 0) {
		if (exp & 1) {
			result = (uint64_t)(((__uint128_t)result * base) % mod);
		}
		base = (uint64_t)(((__uint128_t)base * base) % mod);
		exp /= 2;
	}

	return result;
}

static inline void uint64_to_le_bytes(uint64_t value, uint8_t *bytes) {
	for (int i = 0; i < 8; i++) {
		bytes[i] = value & 0xFF;
		value >>= 8;
	}
}

static inline uint64_t le_bytes_to_uint64(const uint8_t *bytes) {
	uint64_t value = 0;
	for (int i = 7; i >= 0; i--)
		value = (value << 8) | bytes[i];
	return value;
}

static inline void aes_single_round(uint8_t *block, const uint8_t *key) {
	__m128i block_vec = _mm_loadu_si128((const __m128i *)block);
	__m128i key_vec = _mm_loadu_si128((const __m128i *)key);

	block_vec = _mm_aesenc_si128(block_vec, key_vec);

	_mm_storeu_si128((__m128i *)block, block_vec);
}

static void stage3(uint64_t *scratch) {
	uint64_t *mem_buffer_a = scratch;
	uint64_t *mem_buffer_b = &scratch[BUFSIZE];

	uint64_t addr_a = mem_buffer_b[BUFSIZE - 1];
	uint64_t addr_b = mem_buffer_a[BUFSIZE - 1] >> 32;
	uint32_t r = 0;

	for (uint32_t i = 0; i < ITERS; i++) {
		uint64_t mem_a = mem_buffer_a[map_index(addr_a)];
		uint64_t mem_b = mem_buffer_b[map_index(mem_a ^ addr_b)];

		uint8_t block[16];
		uint64_to_le_bytes(mem_b, block);
		uint64_to_le_bytes(mem_a, block + 8);
		aes_single_round(block, (const uint8_t *)KEY);

		uint64_t hash1 = le_bytes_to_uint64(block);
		uint64_t hash2 = le_bytes_to_uint64(block + 8);
		uint64_t result = ~(hash1 ^ hash2);

		for (uint32_t j = 0; j < BUFSIZE; j++) {
			uint64_t a = mem_buffer_a[map_index(result)];
			uint64_t b = mem_buffer_b[map_index(a ^ ~ROTR(result, r))];
			uint64_t c = (r < BUFSIZE) ? mem_buffer_a[r] : mem_buffer_b[r - BUFSIZE];
			r = (r < MEMSIZE - 1) ? r + 1 : 0;

			uint64_t v;
			__uint128_t t1, t2;
			switch (ROTL(result, (uint32_t)c) & 0xf) {
			case 0: {
				t1 = combine_uint64(a + i, isqrt(b + j));
				uint64_t denom = murmurhash3(c ^ result ^ i ^ j) | 1;
				v = (uint64_t)(t1 % denom);
				break;
			}
			case 1:
				v = ROTL((c + i) % isqrt(b | 2), i + j) * isqrt(a + j);
				break;
			case 2:
				v = (isqrt(a + i) * isqrt(c + j)) ^ (b + i + j);
				break;
			case 3:
				v = ((a + b) * c);
				break;
			case 4:
				v = ((b - c) * a);
				break;
			case 5:
				v = (c - a + b);
				break;
			case 6:
				v = (a - b + c);
				break;
			case 7:
				v = (b * c + a);
				break;
			case 8:
				v = (c * a + b);
				break;
			case 9:
				v = (a * b * c);
				break;
			case 10:
				t1 = combine_uint64(a, b);
				v = t1 % (c | 1);
				break;
			case 11:
				t1 = combine_uint64(b, c);
				t2 = combine_uint64(ROTL(result, r), a | 2);
				v = (t2 > t1) ? c : t1 % t2;
				break;
			case 12:
				v = udiv(c, a, b | 4);
				break;
			case 13:
				t1 = combine_uint64(ROTL(result, r), b);
				t2 = combine_uint64(a, c | 8);
				v = (t1 > t2) ? t1 / t2 : a ^ b;
				break;
			case 14:
				t1 = combine_uint64(b, a);
				v = (t1 * c) >> 64;
				break;
			case 15:
				t1 = combine_uint64(a, c);
				t2 = combine_uint64(ROTR(result, r), b);
				v = (t1 * t2) >> 64;
				break;
			default:
				v = 0; /* unreachable: the switch covers 0..15 */
				break;
			}
			uint64_t idx_seed = v ^ result;
			result = ROTL(idx_seed, r);

			uint64_t use_buffer_b = pick_half(v);
			uint64_t idx_t = map_index(idx_seed);
			uint64_t t = (use_buffer_b ? mem_buffer_b[idx_t] : mem_buffer_a[idx_t]) ^ result;

			uint64_t idx_a = map_index(t ^ result ^ 0x9e3779b97f4a7c15);
			uint64_t idx_b = map_index(idx_a ^ ~result ^ 0xd2b74407b1ce6e93);

			uint64_t mem_a = mem_buffer_a[idx_a];
			mem_buffer_a[idx_a] = t;
			mem_buffer_b[idx_b] ^= mem_a ^ ROTR(t, i + j);
		}

		addr_a = modular_power(addr_a, addr_b, result);
		addr_b = isqrt(result) * (r + 1) * isqrt(addr_a);
	}
}

static void xelis_hash_v3_scratch(const uint8_t in[INPUT_LEN], uint8_t hash[HASH_SIZE], uint64_t scratch[MEMSIZE]) {
	uint8_t *scratch_uint8 = (uint8_t *)scratch;

	stage1(in, scratch_uint8);
	stage3(scratch);
	blake3(scratch_uint8, OUTPUT_SIZE, hash);
}

/* libmultihash entry point (exports.cpp: xelis_hash_v3_export). Thread-local scratchpad: Miningcore hashes shares from
 * many threads; one 531 KiB buffer per thread is cheaper than a calloc per share and safe without locks. */
void xelis_hash_v3(const unsigned char *input, uint32_t inputLen, unsigned char *hashResult)
{
	static __thread uint64_t scratch[MEMSIZE];

	if (inputLen != INPUT_LEN) {
		memset(hashResult, 0, HASH_SIZE);
		return;
	}

	xelis_hash_v3_scratch(input, hashResult, scratch);
}
