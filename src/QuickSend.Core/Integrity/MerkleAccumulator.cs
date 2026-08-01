using System.Security.Cryptography;

namespace Eslee.QuickSend.Core.Integrity;

public sealed class MerkleAccumulator
{
    private readonly List<byte[]> _leaves = [];

    public int LeafCount => _leaves.Count;

    public void AddChunk(ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([0]);
        hash.AppendData(data);
        _leaves.Add(hash.GetHashAndReset());
    }

    public void AddLeafHash(ReadOnlySpan<byte> leaf)
    {
        if (leaf.Length != 32)
            throw new ArgumentException("A SHA-256 Merkle leaf must contain 32 bytes.", nameof(leaf));
        _leaves.Add(leaf.ToArray());
    }

    public byte[] ComputeRoot()
    {
        if (_leaves.Count == 0)
            return SHA256.HashData([0]);

        var level = _leaves.Select(static leaf => leaf.ToArray()).ToList();
        var pair = new byte[65];
        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : left;
                pair[0] = 1;
                left.CopyTo(pair, 1);
                right.CopyTo(pair, 33);
                next.Add(SHA256.HashData(pair));
            }
            level = next;
        }
        return level[0];
    }

    public byte[] ExportLeaves()
    {
        var result = new byte[_leaves.Count * 32];
        for (var i = 0; i < _leaves.Count; i++)
            _leaves[i].CopyTo(result, i * 32);
        return result;
    }

    public static MerkleAccumulator ImportLeaves(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length % 32 != 0)
            throw new ArgumentException("Invalid Merkle leaf snapshot.", nameof(snapshot));
        var accumulator = new MerkleAccumulator();
        for (var offset = 0; offset < snapshot.Length; offset += 32)
            accumulator.AddLeafHash(snapshot.Slice(offset, 32));
        return accumulator;
    }
}
