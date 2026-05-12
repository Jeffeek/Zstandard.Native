// AOT validation probe. The publish workflow builds this project with
// PublishAot=true against the just-packed Zstandard.Native nupkg and then
// runs the resulting native binary. Any failure here -- ILC error, missing
// API, runtime crash, or wrong output -- aborts the NuGet push.

using Zstandard.Native;

var payload = new byte[64 * 1024];
new Random(1).NextBytes(payload);

var bound = ZstdCompressor.GetCompressBound(payload.Length);
var compressed = new byte[bound];
var written = ZstdCompressor.Compress(payload, compressed, compressionLevel: 3);

var roundTrip = new byte[payload.Length];
var decoded = ZstdCompressor.Decompress(compressed.AsSpan(0, written), roundTrip);

if (decoded != payload.Length || !payload.AsSpan().SequenceEqual(roundTrip))
{
    Console.Error.WriteLine("AOT probe: round-trip mismatch.");
    return 2;
}

using var streamer = new ZstdStreamCompressor();
var streamOut = new byte[bound];
var r = streamer.Compress(payload, streamOut, ZstdEndDirective.End);
if (!r.IsCompleted || r.BytesWritten <= 0)
{
    Console.Error.WriteLine("AOT probe: streaming compress did not complete.");
    return 3;
}

Console.WriteLine($"AOT probe OK. accel={HardwareAccelerator.ActiveAccelerator} written={written} stream={r.BytesWritten}");
return 0;
