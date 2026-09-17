using Newtonsoft.Json;

namespace VDGS.Sog
{
    /// <summary>SOG v2 meta.json DTO. Only the fields the decoder reads.</summary>
    public sealed class SogMeta
    {
        [JsonProperty("version")] public int Version;
        [JsonProperty("count")] public int Count;
        [JsonProperty("means")] public SogMeans Means;
        [JsonProperty("scales")] public SogCodebookImages Scales;
        [JsonProperty("quats")] public SogFilesOnly Quats;
        [JsonProperty("sh0")] public SogCodebookImages Sh0;
        [JsonProperty("shN")] public SogShN ShN;
    }

    public sealed class SogMeans
    {
        [JsonProperty("mins")] public double[] Mins;
        [JsonProperty("maxs")] public double[] Maxs;
        [JsonProperty("files")] public string[] Files;
    }

    public sealed class SogCodebookImages
    {
        [JsonProperty("codebook")] public double[] Codebook;
        [JsonProperty("files")] public string[] Files;
    }

    public sealed class SogFilesOnly
    {
        [JsonProperty("files")] public string[] Files;
    }

    public sealed class SogShN
    {
        [JsonProperty("count")] public int Count;
        [JsonProperty("bands")] public int Bands;
        [JsonProperty("codebook")] public double[] Codebook;
        [JsonProperty("files")] public string[] Files;
    }
}
