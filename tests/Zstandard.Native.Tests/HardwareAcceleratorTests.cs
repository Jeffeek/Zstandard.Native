using Xunit;

namespace Zstandard.Native.Tests;

// ReSharper disable once ClassCanBeSealed.Global
public class HardwareAcceleratorTests
{
    [
        Theory,
        InlineData(0),
        InlineData(1),
        InlineData(31),
        InlineData(32),
        InlineData(63),
        InlineData(64),
        InlineData(65),
        InlineData(127),
        InlineData(4096),
        InlineData(4097)
    ]
    public void ClearBuffer_Zeroes_All_Bytes(int size)
    {
        var buf = new byte[size];
        Array.Fill(buf, (byte)0xAB);

        HardwareAccelerator.ClearBuffer(buf);

        Assert.All(buf, static b => Assert.Equal(0, b));
    }

    [Fact]
    public void Active_Accelerator_Matches_Flag()
    {
        if (HardwareAccelerator.IsHardwareAccelerated)
            Assert.NotEqual(AcceleratorKind.None, HardwareAccelerator.ActiveAccelerator);
        else
            Assert.Equal(AcceleratorKind.None, HardwareAccelerator.ActiveAccelerator);
    }
}
