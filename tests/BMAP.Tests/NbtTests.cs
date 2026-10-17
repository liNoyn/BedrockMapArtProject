using BMAP.Nbt;

namespace BMAP.Tests;

public class NbtTests
{
    [Fact] // T19
    public void Nbt_IsLittleEndian_AndRoundTrips()
    {
        var c = new NbtCompound
        {
            { "i", new NbtInt(0x01020304) },
            { "l", new NbtLong(-5) },
            { "s", new NbtString("日本語") },
            { "list", new NbtList(NbtType.Compound) },
            { "f", new NbtFloat(1.5f) },
            { "ia", new NbtIntArray([1, 2, 3]) },
        };
        var bytes = NbtIO.Write(c);
        // tag(1) + name len(2) + "" + [tag(1) + len(2) + "i" + payload]
        Assert.Equal(new byte[] { 3, 1, 0, (byte)'i', 4, 3, 2, 1 }, bytes[3..11]);
        Assert.Equal(bytes, NbtIO.Write(NbtIO.Read(bytes)));
    }
}
