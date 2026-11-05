using System.Security.Cryptography;
using Miningcore.Mining;
using Miningcore.Util;
using NLog;

namespace Miningcore.Blockchain;

public class ExtraNonceProviderBase : IExtraNonceProvider
{
    protected ExtraNonceProviderBase(string poolId, int extranonceBytes, byte? instanceId, int idBits = DefaultIdBits)
    {
        logger = LogUtil.GetPoolScopedLogger(GetType(), poolId);

        this.extranonceBytes = extranonceBytes;
        idShift = (extranonceBytes * 8) - idBits;
        nonceMax = (1UL << idShift) - 1;
        idMax = (1U << idBits) - 1;
        stringFormat = "x" + extranonceBytes * 2;

        // generate instanceId if not provided
        var mask = (1L << idBits) - 1;

        if(instanceId.HasValue)
        {
            id = instanceId.Value;

            if(id > idMax)
                throw new PoolStartupException($"Provided instance id too large to fit into {idBits} bits (limit {idMax})", poolId);
        }

        else
        {
            using(var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[1];
                rng.GetNonZeroBytes(bytes);
                id = bytes[0];
            }
        }

        id = (byte) (id & mask);
        counter = 0;

        logger.Info(()=> $"ExtraNonceProvider using {idBits} bits for instance id, {extranonceBytes * 8 - idBits} bits for {nonceMax} values, instance id = 0x{id:X}");
    }

    private readonly ILogger logger;

    private const int DefaultIdBits = 4;
    private readonly object counterLock = new();
    protected ulong counter;
    protected byte id;
    protected readonly int extranonceBytes;
    protected readonly int idShift;
    protected readonly uint idMax;
    protected readonly ulong nonceMax;
    protected readonly string stringFormat;

    #region IExtraNonceProvider

    public int ByteSize => extranonceBytes;

    public ulong Capacity => nonceMax + 1;

    public string Next()
    {
        ulong value;

        lock(counterLock)
        {
            counter++;

            if(counter > nonceMax)
            {
                logger.Warn(()=> $"ExtraNonceProvider range exhausted! Rolling over to 0.");

                counter = 0;
            }

            // encode to hex
            value = ((ulong) id << idShift) | counter;
        }

        return value.ToString(stringFormat);
    }

    #endregion // IExtraNonceProvider
}
