using Newtonsoft.Json;

namespace Miningcore.Blockchain.Xelis.DaemonRequests;

public class BuildTransactionTransfer
{
    public ulong Amount { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Asset { get; set; }

    public string Destination { get; set; }

    [JsonProperty("extra_data", NullValueHandling = NullValueHandling.Ignore)]
    public string ExtraData { get; set; }
}

/// <summary>
/// xelis_common FeeBuilder (serde snake_case, externally tagged): {"fixed": u64} or {"extra": …}.
/// hashlak: upstream sent {"value": …} / {"multiplier": …}, the pre-1.2x API that xelis_wallet 1.25 refuses — a fixed fee
/// never reached the wallet (mainnet 2026-11-03).
/// </summary>
public class BuildTransactionFee
{
    [JsonProperty("fixed", NullValueHandling = NullValueHandling.Ignore)]
    public ulong? Fixed { get; set; }
}

public class BuildTransactionRequest
{
    public BuildTransactionTransfer[] Transfers { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public BuildTransactionFee Fee { get; set; }

    public bool broadcast { get; set; } = true;

    [JsonProperty("tx_as_hex")]
    public bool TransactionAsHexadecimal { get; set; } = true;
}

public class EstimateFeesRequest
{
    public BuildTransactionTransfer[] Transfers { get; set; }
}
