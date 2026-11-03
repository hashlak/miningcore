using System;
using System.Numerics;
using Miningcore.Extensions;

namespace Miningcore.Blockchain.Xelis;

public static class XelisUtils
{
    /// <summary>
    /// Block target exactly as xelis_common::difficulty::compute_difficulty_target: U256::MAX / difficulty
    /// (integer division; the daemon's difficulty is an integer). Zero for a zero difficulty.
    /// </summary>
    public static BigInteger DifficultyToTargetValue(double difficulty)
    {
        var diff = new BigInteger(Math.Floor(difficulty));

        return diff.Sign <= 0 ? BigInteger.Zero : XelisConstants.Diff1Target / diff;
    }

    /// <summary>
    /// hashlak: the target as 32 big-endian bytes. The upstream version right-aligned BigInteger.ToByteArray(), which is
    /// little-endian, so every reader that parsed it big-endian got a target driven by its lowest byte: on mainnet
    /// (2026-11-03) ordinary shares were submitted as blocks ("Invalid difficulty") and some real blocks were never submitted.
    /// </summary>
    public static byte[] DifficultyToTarget(double difficulty)
    {
        var paddedBytes = new byte[XelisConstants.TargetPaddingLength];
        var targetBytes = DifficultyToTargetValue(difficulty).ToByteArray(isUnsigned: true, isBigEndian: true);

        if(targetBytes.Length == 1 && targetBytes[0] == 0)
            return paddedBytes;

        targetBytes.CopyTo(paddedBytes, paddedBytes.Length - targetBytes.Length);

        return paddedBytes;
    }

    /// <summary>
    /// xelis_common::difficulty::check_difficulty_against_target: U256::from_big_endian(hash) &lt;= target.
    /// </summary>
    public static bool MeetsTarget(ReadOnlySpan<byte> hash, BigInteger target)
    {
        return new BigInteger(hash, isUnsigned: true, isBigEndian: true) <= target;
    }

    public static long UnixTimeStamp(DateTime date)
    {
        long unixTimeStamp = date.ToUniversalTime().Ticks - new DateTime(1970, 1, 1, 0, 0, 0).Ticks;
        unixTimeStamp /= TimeSpan.TicksPerMillisecond;
        return unixTimeStamp;
    }

    public static bool CheckDiff(Span<byte> hashBytes, double difficulty)
    {
        byte[] targetBytes = DifficultyToTarget(difficulty);

        return CheckDiff(hashBytes, targetBytes);
    }

    public static bool CheckDiff(Span<byte> hashBytes, byte[] targetBytes)
    {
        for (int i = 0; i < 32; i++)
        {
            if (hashBytes[i] < targetBytes[i])
                return true;

            if (hashBytes[i] > targetBytes[i])
                return false;
        }

        return true;    // hash == target meets it (the daemon compares with <=)
    }
}
