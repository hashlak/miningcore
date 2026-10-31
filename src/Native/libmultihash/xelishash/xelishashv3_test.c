/*
 * Gold-vector test for xelishashv3.c against the official xelis-hash test suite (src/v3.rs: test_zero_hash,
 * test_verify_output). Build and run from src/Native/libmultihash (x86-64, gcc):
 *
 *   gcc -O2 -maes -msse2 -mssse3 -mpclmul -mavx -mavx2 \
 *       xelishash/xelishashv3.c xelishash/xelishashv3_test.c chacha20/chacha20.c \
 *       blake3/blake3.c blake3/blake3_dispatch.c blake3/blake3_portable.c \
 *       blake3/blake3_sse2_x86-64_unix.S blake3/blake3_sse41_x86-64_unix.S \
 *       blake3/blake3_avx2_x86-64_unix.S blake3/blake3_avx512_x86-64_unix.S \
 *       -lm -o /tmp/xelishashv3_test && /tmp/xelishashv3_test
 *
 * Exit code 0 = both vectors match. Also reports hashes/s for one thread (scratchpad reuse).
 */
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

void xelis_hash_v3(const unsigned char *input, uint32_t inputLen, unsigned char *hashResult);

static const uint8_t EXPECTED_ZERO[32] = {
	105, 172, 103, 40, 94, 253, 92, 162, 42, 252, 5, 196, 236, 238, 91, 218,
	22, 157, 228, 233, 239, 8, 250, 57, 212, 166, 121, 132, 148, 205, 103, 163
};

static const uint8_t INPUT_2[112] = {
	172, 236, 108, 212, 181, 31, 109, 45, 44, 242, 54, 225, 143, 133,
	89, 44, 179, 108, 39, 191, 32, 116, 229, 33, 63, 130, 33, 120, 185, 89,
	146, 141, 10, 79, 183, 107, 238, 122, 92, 222, 25, 134, 90, 107, 116,
	110, 236, 53, 255, 5, 214, 126, 24, 216, 97, 199, 148, 239, 253, 102,
	199, 184, 232, 253, 158, 145, 86, 187, 112, 81, 78, 70, 80, 110, 33,
	37, 159, 233, 198, 1, 178, 108, 210, 100, 109, 155, 106, 124, 124, 83,
	89, 50, 197, 115, 231, 32, 74, 2, 92, 47, 25, 220, 135, 249, 122,
	172, 220, 137, 143, 234, 68, 188
};

static const uint8_t EXPECTED_2[32] = {
	242, 8, 176, 222, 203, 27, 104, 187, 22, 40, 68, 73, 79, 79, 65, 83,
	138, 101, 10, 116, 194, 41, 153, 21, 92, 163, 12, 206, 231, 156, 70, 83
};

static void print_hex(const char *label, const uint8_t *h) {
	printf("%s", label);
	for (int i = 0; i < 32; i++) printf("%02x", h[i]);
	printf("\n");
}

static int check(const char *name, const uint8_t *input, const uint8_t *expected) {
	uint8_t hash[32];
	xelis_hash_v3(input, 112, hash);
	int ok = memcmp(hash, expected, 32) == 0;
	printf("%s: %s\n", name, ok ? "PASS" : "FAIL");
	if (!ok) { print_hex("  got      ", hash); print_hex("  expected ", expected); }
	return ok;
}

int main(void) {
	uint8_t zero[112] = {0};
	int ok = 1;
	ok &= check("zero input   ", zero, EXPECTED_ZERO);
	ok &= check("verify_output", INPUT_2, EXPECTED_2);
	/* second run on a dirty scratchpad must give the same hash (xelis-hash test_scratch_pad) */
	ok &= check("dirty scratch", zero, EXPECTED_ZERO);

	uint8_t hash[32];
	uint8_t in[112];
	memcpy(in, INPUT_2, 112);
	const int n = 200;
	struct timespec t0, t1;
	clock_gettime(CLOCK_MONOTONIC, &t0);
	for (int i = 0; i < n; i++) { in[40] = (uint8_t)i; xelis_hash_v3(in, 112, hash); }
	clock_gettime(CLOCK_MONOTONIC, &t1);
	double secs = (t1.tv_sec - t0.tv_sec) + (t1.tv_nsec - t0.tv_nsec) / 1e9;
	printf("throughput: %.0f hashes/s on one thread\n", n / secs);
	return ok ? 0 : 1;
}
