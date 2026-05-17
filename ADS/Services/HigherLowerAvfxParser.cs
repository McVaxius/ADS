using System.Buffers.Binary;
using System.Text;

namespace ADS.Services;

public static class HigherLowerAvfxParser
{
    private static readonly HashSet<string> ParticleTextureBlocks = new(StringComparer.Ordinal)
    {
        "TC1",
        "TC2",
        "TC3",
        "TC4",
    };

    public static bool TryParse(byte[] data, out HigherLowerAvfxMetadata metadata, out string error)
    {
        metadata = HigherLowerAvfxMetadata.Empty;
        error = string.Empty;

        try
        {
            if (data.Length < 8)
            {
                error = "file too small";
                return false;
            }

            var rootName = ReadName(data, 0);
            if (!string.Equals(rootName, "AVFX", StringComparison.Ordinal))
            {
                error = $"root block was '{rootName}'";
                return false;
            }

            var rootSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
            if (rootSize < 0 || 8 + rootSize > data.Length)
            {
                error = $"invalid root size {rootSize}";
                return false;
            }

            var texturePaths = new List<string>();
            var textureIndexes = new List<ParsedTextureIndex>();
            ParseRoot(data, 8, 8 + rootSize, texturePaths, textureIndexes);

            metadata = new HigherLowerAvfxMetadata(
                texturePaths,
                textureIndexes
                    .Select(x => new HigherLowerAvfxTextureIndex(
                        x.Source,
                        x.Value,
                        ResolveTexturePath(texturePaths, x.Value),
                        x.Offset))
                    .ToList());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ParseRoot(
        ReadOnlySpan<byte> data,
        int start,
        int end,
        List<string> texturePaths,
        List<ParsedTextureIndex> textureIndexes)
    {
        var offset = start;
        var particleIndex = 0;
        while (TryReadBlock(data, offset, end, out var block, out var nextOffset))
        {
            if (string.Equals(block.Name, "Tex", StringComparison.Ordinal))
            {
                texturePaths.Add(ReadNullTerminatedString(data[block.ContentStart..block.ContentEnd]));
            }
            else if (string.Equals(block.Name, "Ptcl", StringComparison.Ordinal))
            {
                ParseParticle(data, block.ContentStart, block.ContentEnd, particleIndex, textureIndexes);
                particleIndex++;
            }

            offset = nextOffset;
        }
    }

    private static void ParseParticle(
        ReadOnlySpan<byte> data,
        int start,
        int end,
        int particleIndex,
        List<ParsedTextureIndex> textureIndexes)
    {
        var offset = start;
        while (TryReadBlock(data, offset, end, out var block, out var nextOffset))
        {
            if (ParticleTextureBlocks.Contains(block.Name))
                ParseParticleTextureBlock(data, block, particleIndex, textureIndexes);

            offset = nextOffset;
        }
    }

    private static void ParseParticleTextureBlock(
        ReadOnlySpan<byte> data,
        AvfxBlock textureBlock,
        int particleIndex,
        List<ParsedTextureIndex> textureIndexes)
    {
        var offset = textureBlock.ContentStart;
        while (TryReadBlock(data, offset, textureBlock.ContentEnd, out var field, out var nextOffset))
        {
            if (string.Equals(field.Name, "TxNo", StringComparison.Ordinal)
                && TryReadInt(data[field.ContentStart..field.ContentEnd], out var textureIndex))
            {
                textureIndexes.Add(new ParsedTextureIndex(
                    $"Ptcl[{particleIndex}].{textureBlock.Name}.TxNo",
                    textureIndex,
                    field.ContentStart));
            }

            offset = nextOffset;
        }
    }

    private static bool TryReadBlock(
        ReadOnlySpan<byte> data,
        int offset,
        int end,
        out AvfxBlock block,
        out int nextOffset)
    {
        block = default;
        nextOffset = offset;

        if (offset + 8 > end)
            return false;

        var name = ReadName(data, offset);
        var size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
        if (size < 0)
            throw new InvalidDataException($"negative AVFX block size at 0x{offset:X}");

        var contentStart = offset + 8;
        var contentEnd = contentStart + size;
        if (contentEnd > end)
            throw new InvalidDataException($"AVFX block '{name}' at 0x{offset:X} overruns parent");

        nextOffset = contentEnd + CalculatePadding(size);
        if (nextOffset > end)
            nextOffset = contentEnd;

        block = new AvfxBlock(name, contentStart, contentEnd);
        return true;
    }

    private static string ReadName(ReadOnlySpan<byte> data, int offset)
    {
        Span<byte> reversed = stackalloc byte[4];
        reversed[0] = data[offset + 3];
        reversed[1] = data[offset + 2];
        reversed[2] = data[offset + 1];
        reversed[3] = data[offset];

        Span<byte> compact = stackalloc byte[4];
        var length = 0;
        for (var i = 0; i < reversed.Length; i++)
        {
            if (reversed[i] != 0)
                compact[length++] = reversed[i];
        }

        return Encoding.ASCII.GetString(compact[..length]);
    }

    private static string ReadNullTerminatedString(ReadOnlySpan<byte> data)
    {
        var length = data.IndexOf((byte)0);
        if (length < 0)
            length = data.Length;

        return Encoding.UTF8.GetString(data[..length]).Trim();
    }

    private static bool TryReadInt(ReadOnlySpan<byte> data, out int value)
    {
        value = 0;
        switch (data.Length)
        {
            case >= 4:
                value = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
                return true;
            case 2:
                value = BinaryPrimitives.ReadInt16LittleEndian(data);
                return true;
            case 1:
                value = data[0];
                return true;
            default:
                return false;
        }
    }

    private static string ResolveTexturePath(IReadOnlyList<string> texturePaths, int textureIndex)
        => textureIndex >= 0 && textureIndex < texturePaths.Count ? texturePaths[textureIndex] : string.Empty;

    private static int CalculatePadding(int size)
        => size % 4 == 0 ? 0 : 4 - (size % 4);

    private readonly record struct AvfxBlock(string Name, int ContentStart, int ContentEnd);

    private sealed record ParsedTextureIndex(string Source, int Value, int Offset);
}

public sealed record HigherLowerAvfxMetadata(
    IReadOnlyList<string> TexturePaths,
    IReadOnlyList<HigherLowerAvfxTextureIndex> TextureIndexes)
{
    public static HigherLowerAvfxMetadata Empty { get; } = new([], []);
}

public sealed record HigherLowerAvfxTextureIndex(
    string Source,
    int Value,
    string TexturePath,
    int Offset);
