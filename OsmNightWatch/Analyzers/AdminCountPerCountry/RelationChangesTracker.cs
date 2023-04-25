﻿using OsmNightWatch.PbfParsing;
using System.Buffers.Binary;

namespace OsmNightWatch.Analyzers.AdminCountPerCountry;

public class RelationChangesTracker
{
    Dictionary<long, HashSet<uint>> NodeToWay = new();
    Dictionary<uint, HashSet<uint>> WayToRelation = new();
    HashSet<long> Relations = new();

    public void AddRelation(long relationId, List<Way> ways, IOsmGeoSource newOsmSource)
    {
        lock (Relations)
        {
            Relations.Add(relationId);
            foreach (var way in ways)
            {
                if (WayToRelation.TryGetValue((uint)way.Id!, out var relations))
                {
                    relations.Add((uint)relationId);
                }
                else
                {
                    WayToRelation.Add((uint)way.Id!, new HashSet<uint>() { (uint)relationId });
                    foreach (var node in way.Nodes)
                    {
                        if (NodeToWay.TryGetValue(node, out var nodeToWayWays))
                        {
                            nodeToWayWays.Add((uint)way.Id!);
                        }
                        else
                        {
                            NodeToWay.Add(node, new HashSet<uint>() { (uint)way.Id! });
                        }
                    }
                }
            }
        }
    }

    public RelationChangesTracker(string? existingPath = null)
    {
        if (string.IsNullOrEmpty(existingPath))
        {
            return;
        }

        ReadOnlySpan<byte> span = File.ReadAllBytes(existingPath);
        var relationsCount = BinSerialize.ReadUInt(ref span);
        for (int i = 0; i < relationsCount; i++)
        {
            Relations.Add(BinSerialize.ReadUInt(ref span));
        }
        var wayToRelationCount = BinSerialize.ReadUInt(ref span);
        for (int i = 0; i < wayToRelationCount; i++)
        {
            var way = BinSerialize.ReadUInt(ref span);
            var hashset = new HashSet<uint>();
            var count = BinSerialize.ReadByte(ref span);
            for (int j = 0; j < count; j++)
            {
                hashset.Add(BinSerialize.ReadUInt(ref span));
            }
            WayToRelation.Add(way, hashset);
        }
        var nodeToWayCount = BinSerialize.ReadUInt(ref span);
        for (int i = 0; i < nodeToWayCount; i++)
        {
            var node = BinSerialize.ReadLong(ref span);
            var hashset = new HashSet<uint>();
            var count = BinSerialize.ReadByte(ref span);
            for (int j = 0; j < count; j++)
            {
                hashset.Add(BinSerialize.ReadUInt(ref span));
            }
            NodeToWay.Add(node, hashset);
        }
    }

    public void Serialize(string filePath)
    {
        using var file = File.Create(filePath);
        Span<byte> buffer = new byte[4 * (Relations.Count + 1)];
        var span = buffer;
        BinSerialize.WriteUInt(ref span, (uint)Relations.Count);
        foreach (var relation in Relations)
        {
            BinSerialize.WriteUInt(ref span, (uint)relation);
        }
        file.Write(buffer);

        span = buffer;
        BinSerialize.WriteUInt(ref span, (uint)WayToRelation.Count);
        file.Write(buffer.Slice(0, 4));
        foreach (var relation in WayToRelation)
        {
            span = buffer;
            BinSerialize.WriteUInt(ref span, relation.Key);
            BinSerialize.WriteByte(ref span, (byte)relation.Value.Count);
            foreach (var item in relation.Value)
            {
                BinSerialize.WriteUInt(ref span, item);
            }
            file.Write(buffer.Slice(0, buffer.Length - span.Length));

        }

        span = buffer;
        BinSerialize.WriteUInt(ref span, (uint)NodeToWay.Count);
        file.Write(buffer.Slice(0, 4));
        foreach (var relation in NodeToWay)
        {
            span = buffer;
            BinSerialize.WriteLong(ref span, relation.Key);
            BinSerialize.WriteByte(ref span, (byte)relation.Value.Count);
            foreach (var item in relation.Value)
            {
                BinSerialize.WriteUInt(ref span, item);
            }
            file.Write(buffer.Slice(0, buffer.Length - span.Length));
        }
    }


    public HashSet<long> GetChangedRelations(MergedChangeset changeSet)
    {
        var result = new HashSet<long>();
        foreach (var node in changeSet.Nodes.Keys)
        {
            if (NodeToWay.TryGetValue(node, out var ways))
            {
                foreach (var way in ways)
                {
                    if (WayToRelation.TryGetValue(way, out var relations))
                    {
                        foreach (var relation in relations)
                        {
                            result.Add(relation);
                        }
                    }
                }
            }
        }

        foreach (var way in changeSet.Ways.Keys)
        {
            if (WayToRelation.TryGetValue((uint)way, out var relations))
            {
                foreach (var relation in relations)
                {
                    result.Add(relation);
                }
            }
        }

        foreach (var relation in changeSet.Relations.Keys)
        {
            if (Relations.Contains((uint)relation))
            {
                result.Add(relation);
            }
        }

        return result;
    }
}
