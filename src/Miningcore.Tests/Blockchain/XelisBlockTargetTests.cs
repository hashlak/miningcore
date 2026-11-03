using System.Numerics;
using Miningcore.Blockchain.Xelis;
using Miningcore.Extensions;
using Xunit;

namespace Miningcore.Tests.Blockchain;

public class XelisBlockTargetTests
{
    private static readonly BigInteger U256Max = BigInteger.Pow(2, 256) - 1;

    // Mainnet 2026-11-03 14:04:48Z: the pool submitted this share as block 9745458 and the daemon answered
    // "Invalid difficulty (Code -32004)". Hash difficulty 88,317,950; the block at the same tip needed 206,836,845.
    private const string RejectedShareHash = "00000030a1783177f51c24762506f589642a20b3772ced52fa38dc87dec42e22";
    private const double TipDifficulty = 206836845;

    [Fact]
    public void Target_Matches_Daemon_Formula()
    {
        Assert.Equal(U256Max / 206836845, XelisUtils.DifficultyToTargetValue(TipDifficulty));
        Assert.Equal(U256Max, XelisUtils.DifficultyToTargetValue(1));
        Assert.Equal(BigInteger.Zero, XelisUtils.DifficultyToTargetValue(0));
    }

    [Fact]
    public void Rejected_Mainnet_Share_Is_Not_A_Block_Candidate()
    {
        var hash = RejectedShareHash.HexToByteArray();

        Assert.False(XelisUtils.MeetsTarget(hash, XelisUtils.DifficultyToTargetValue(TipDifficulty)));
        Assert.False(XelisUtils.CheckDiff(hash, XelisUtils.DifficultyToTarget(TipDifficulty)));

        // it is still a valid share at the farm port's difficulty
        Assert.True(XelisUtils.MeetsTarget(hash, XelisUtils.DifficultyToTargetValue(500000)));
        Assert.True(XelisUtils.MeetsTarget(hash, XelisUtils.DifficultyToTargetValue(88317950)));
        Assert.False(XelisUtils.MeetsTarget(hash, XelisUtils.DifficultyToTargetValue(88317951)));
    }

    [Fact]
    public void Target_Bytes_Are_Big_Endian_And_Boundaries_Use_LessOrEqual()
    {
        foreach(var difficulty in new double[] { 1, 255, 65536, 20000, 500000, 199171405, 204656790, TipDifficulty, 271487510, 9.0e15 })
        {
            var target = XelisUtils.DifficultyToTargetValue(difficulty);
            var bytes = XelisUtils.DifficultyToTarget(difficulty);

            Assert.Equal(32, bytes.Length);
            Assert.Equal(target, new BigInteger(bytes, isUnsigned: true, isBigEndian: true));

            var atTarget = bytes;
            Assert.True(XelisUtils.MeetsTarget(atTarget, target));
            Assert.True(XelisUtils.CheckDiff(atTarget, bytes));

            if(target == U256Max)
                continue;   // every hash meets difficulty 1

            var aboveTarget = (target + 1).ToByteArray(isUnsigned: true, isBigEndian: true);
            var padded = new byte[32];
            aboveTarget.CopyTo(padded, 32 - aboveTarget.Length);
            Assert.False(XelisUtils.MeetsTarget(padded, target));
            Assert.False(XelisUtils.CheckDiff(padded, bytes));
        }
    }
}
