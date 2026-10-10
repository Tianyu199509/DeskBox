using DeskBox.Helpers;

namespace DeskBox.Tests;

/// <summary>
/// Pure-parser tests for the CFSTR_SHELLIDLIST (CIDA) clipboard payload.
/// Buffers are constructed byte-for-byte so the layout contract is pinned
/// without needing the Shell on the test host.
/// </summary>
public sealed class ShellIdListArrayParserTests
{
    [Fact]
    public void TryParse_AcceptsWellFormedMultiChildPayload()
    {
        byte[] buffer = BuildCida(
            EncodePidl(itemSizes: 6),
            EncodePidl(itemSizes: 8),
            EncodePidl(itemSizes: 10));

        bool parsed = ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout layout);

        Assert.True(parsed);
        Assert.Equal(2, layout.ChildOffsets.Length);
        // Every offset must land inside the buffer and past the header.
        Assert.InRange(
            layout.ParentOffset,
            4 + (3 * sizeof(uint)),
            buffer.Length - 1);
        foreach (int childOffset in layout.ChildOffsets)
        {
            Assert.InRange(childOffset, 4 + (3 * sizeof(uint)), buffer.Length - 1);
        }
    }

    [Fact]
    public void TryParse_AcceptsEmptyParentPidlMeaningDesktop()
    {
        // An empty PIDL (lone terminator) is the documented "parent is the
        // desktop" form and must parse like any other.
        byte[] buffer = BuildCida(
            EncodePidl(),
            EncodePidl(itemSizes: 8));

        bool parsed = ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout layout);

        Assert.True(parsed);
        Assert.Single(layout.ChildOffsets);
    }

    [Fact]
    public void TryParse_RejectsTruncatedHeader()
    {
        // cidl claims one child (header needs 12 bytes) but only 8 exist.
        byte[] buffer = new byte[8];
        BitConverter.GetBytes((uint)1).CopyTo(buffer, 0);
        BitConverter.GetBytes(12).CopyTo(buffer, 4);

        Assert.False(ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(unchecked((int)0xFFFFFFFF))]
    public void TryParse_RejectsImpossibleChildCounts(int childCount)
    {
        byte[] buffer = new byte[64];
        BitConverter.GetBytes((uint)childCount).CopyTo(buffer, 0);
        BitConverter.GetBytes((uint)16).CopyTo(buffer, 4);
        BitConverter.GetBytes((uint)24).CopyTo(buffer, 8);

        Assert.False(ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout _));
    }

    [Fact]
    public void TryParse_RejectsOffsetsOutsideThePayload()
    {
        // Parent offset points past the buffer end.
        byte[] buffer = BuildCida(
            EncodePidl(itemSizes: 6),
            EncodePidl(itemSizes: 8));
        BitConverter.GetBytes((uint)buffer.Length + 10).CopyTo(buffer, 4);

        Assert.False(ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout _));
    }

    [Fact]
    public void TryParse_RejectsOffsetsInsideTheHeader()
    {
        byte[] buffer = BuildCida(
            EncodePidl(itemSizes: 6),
            EncodePidl(itemSizes: 8));
        // A child offset naming the cidl field itself (offset 0) must fail:
        // PIDLs only ever live after the offset array.
        BitConverter.GetBytes((uint)0).CopyTo(buffer, 8);

        Assert.False(ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout _));
    }

    [Fact]
    public void TryParse_RejectsUnterminatedPidlChain()
    {
        // The child PIDL's only SHITEMID claims its full 200 bytes and the
        // payload ends right there: no room is left for the two-byte
        // terminator, so the chain never terminates inside the buffer.
        byte[] pidl = new byte[200];
        BitConverter.GetBytes((ushort)200).CopyTo(pidl, 0);
        byte[] buffer = BuildCida(EncodePidl(itemSizes: 6), pidl);

        Assert.False(ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout _));
    }

    [Fact]
    public void TryParse_RejectsShrinkingItemId()
    {
        // A SHITEMID smaller than its own two-byte length field is invalid.
        byte[] pidl = [0x01, 0x00, 0x00, 0x00];
        byte[] buffer = BuildCida(EncodePidl(itemSizes: 6), pidl);

        Assert.False(ShellIdListArrayParser.TryParse(
            buffer,
            out ShellIdListArrayParser.CidaLayout _));
    }

    /// <summary>
    /// Encodes one ITEMIDLIST: a sequence of SHITEMIDs (a USHORT length that
    /// includes itself, followed by that many minus two payload bytes) and a
    /// two-byte zero terminator.
    /// </summary>
    private static byte[] EncodePidl(params ushort[] itemSizes)
    {
        using var stream = new MemoryStream();
        foreach (ushort size in itemSizes)
        {
            stream.Write(BitConverter.GetBytes(size));
            stream.Write(new byte[size - sizeof(ushort)]);
        }

        stream.Write(BitConverter.GetBytes((ushort)0));
        return stream.ToArray();
    }

    /// <summary>
    /// Builds a CIDA: cidl, cidl+1 offsets relative to the structure start
    /// (parent first), then the PIDL bytes themselves.
    /// </summary>
    private static byte[] BuildCida(byte[] parentPidl, params byte[][] childPidls)
    {
        int headerSize = sizeof(uint) + (sizeof(uint) * (childPidls.Length + 1));
        var offsets = new List<int>(childPidls.Length + 1);
        using var body = new MemoryStream();
        int cursor = headerSize;

        foreach (byte[] pidl in new[] { parentPidl }.Concat(childPidls))
        {
            offsets.Add(cursor);
            body.Write(pidl);
            cursor += pidl.Length;
        }

        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes((uint)childPidls.Length));
        foreach (int offset in offsets)
        {
            stream.Write(BitConverter.GetBytes(offset));
        }

        stream.Write(body.ToArray());
        return stream.ToArray();
    }
}
