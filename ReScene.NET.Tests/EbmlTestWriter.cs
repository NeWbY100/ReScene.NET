using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

/// <summary>
/// Primitive EBML byte encoders shared by the MKV-focused app tests. These build the low-level
/// element framing (id + size + payload); each test keeps its own <c>BuildMkv</c> that composes
/// these into the specific document it needs.
/// </summary>
internal static class EbmlTestWriter
{
    /// <summary>Encodes a master element: id + size + concatenated children.</summary>
    public static byte[] Master(byte[] id, params byte[][] children)
    {
        byte[] body = Concat(children);
        return Concat(id, EncodeSize(body.Length), body);
    }

    /// <summary>Encodes a leaf element: id + size + raw payload.</summary>
    public static byte[] Leaf(byte[] id, byte[] payload) => Concat(id, EncodeSize(payload.Length), payload);

    /// <summary>Encodes a leaf element whose payload is the UTF-8 bytes of <paramref name="value"/>.</summary>
    public static byte[] Str(byte[] id, string value) => Leaf(id, System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Encodes an EBML element size (1- or 2-byte vint forms only — sufficient for these tests).</summary>
    public static byte[] EncodeSize(long size) =>
        size < 0x7F ? [(byte)(0x80 | size)] : [(byte)(0x40 | (size >> 8)), (byte)size];

    /// <summary>Concatenates the given byte arrays into one.</summary>
    public static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }

        return result;
    }
}

/// <summary>Tree helpers shared by tests that walk a <see cref="TreeNodeViewModel"/> structure.</summary>
internal static class TreeNodeViewModelExtensions
{
    /// <summary>Depth-first flatten of a node forest (each node followed by its descendants).</summary>
    public static IEnumerable<TreeNodeViewModel> Flatten(this IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            yield return node;
            foreach (TreeNodeViewModel child in node.Children.Flatten())
            {
                yield return child;
            }
        }
    }
}
