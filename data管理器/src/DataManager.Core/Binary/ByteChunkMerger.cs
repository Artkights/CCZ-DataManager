using DataManager.Core.Models;

namespace DataManager.Core.Binary;

internal static class ByteChunkMerger
{
    public static IReadOnlyList<ByteChunk> MergeAdjacent(IEnumerable<ByteChunk> input)
    {
        var ordered = input.OrderBy(chunk => chunk.Offset).ToArray();
        var merged = new List<ByteChunk>();
        for (var first = 0; first < ordered.Length;)
        {
            var last = first + 1;
            var length = ordered[first].Length;
            while (last < ordered.Length && ordered[first].Offset + length == ordered[last].Offset)
                length = checked(length + ordered[last++].Length);
            if (last == first + 1) merged.Add(ordered[first]);
            else
            {
                var before = new byte[length];
                var after = new byte[length];
                var at = 0;
                for (var index = first; index < last; index++)
                {
                    ordered[index].OldBytes.CopyTo(before, at);
                    ordered[index].NewBytes.CopyTo(after, at);
                    at += ordered[index].Length;
                }
                merged.Add(new ByteChunk(ordered[first].Offset, before, after));
            }
            first = last;
        }
        return merged;
    }
}
