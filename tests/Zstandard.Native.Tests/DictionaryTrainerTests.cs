using Xunit;

namespace Zstandard.Native.Tests;

// ReSharper disable once ClassCanBeSealed.Global
public class DictionaryTrainerTests
{
    [Fact]
    public void Train_Produces_Buffer_Starting_With_Dictionary_Magic()
    {
        var (samples, sizes) = BuildJsonCorpus(samples: 256);

        var dict = new byte[ZstdDictionaryTrainer.RecommendedDictionarySize];
        var written = ZstdDictionaryTrainer.Train(samples, sizes, dict);

        Assert.True(written >= 4, "Trained dictionary should include at least the magic.");
        Assert.True(dict.AsSpan(0, 4).SequenceEqual(ZstdDictionaryTrainer.DictionaryMagic),
            "Trained dictionary must begin with the Zstandard dictionary magic 0xEC30A437.");
    }

    [Fact]
    public void Train_Rejects_Empty_Sample_List()
    {
        var dict = new byte[ZstdDictionaryTrainer.RecommendedDictionarySize];
        Assert.Throws<ArgumentException>(() =>
            ZstdDictionaryTrainer.Train([], [], dict));
    }

    [Fact]
    public void Train_Throws_ZstdException_When_Destination_Too_Small()
    {
        var (samples, sizes) = BuildJsonCorpus(samples: 64);
        var tiny = new byte[16];

        Assert.Throws<ZstdException>(() => ZstdDictionaryTrainer.Train(samples, sizes, tiny));
    }

    /// <summary>
    /// Builds a deterministic JSON-ish corpus of <paramref name="samples"/> records,
    /// each shaped <c>{"id":N,"user":"u_X","tags":[...]}</c>. The repeated key/value
    /// substrings give ZDICT enough signal to extract a useful dictionary.
    /// </summary>
    private static (byte[] samples, nuint[] sizes) BuildJsonCorpus(int samples)
    {
        var rng = new Random(2026);
        var buf = new MemoryStream();
        var sizes = new nuint[samples];
        for (var i = 0; i < samples; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"id\":{i},\"user\":\"u_{rng.Next(0, 32)}\",\"role\":\"{(rng.Next(0, 2) == 0 ? "admin" : "viewer")}\",\"tags\":[\"alpha\",\"beta\",\"gamma\"],\"ts\":\"2026-05-12T19:00:{i % 60:00}Z\"}}");
            buf.Write(bytes, 0, bytes.Length);
            sizes[i] = (nuint)bytes.Length;
        }

        return (buf.ToArray(), sizes);
    }
}
