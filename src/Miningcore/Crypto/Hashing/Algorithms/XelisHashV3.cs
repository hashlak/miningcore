using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

/// <summary>
/// XelisHash v3 ("xel/v3", Xelis mainnet since 2025-12-14). Native port of xelis-project/xelis-hash C/xelis_hash_v3.c
/// (src/Native/libmultihash/xelishash/xelishashv3.c). Input is the 112-byte miner work; the output is 32 bytes.
/// </summary>
[Identifier("xelishashv3")]
public unsafe class XelisHashV3 : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);
        Contract.Requires<ArgumentException>(data.Length == 112);

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                Multihash.xelishashv3(input, output, (uint) data.Length);
            }
        }
    }
}
