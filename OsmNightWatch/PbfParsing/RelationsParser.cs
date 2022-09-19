﻿using OsmSharp.Tags;
using OsmSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using static OsmNightWatch.PbfParsing.ParsingHelper;

namespace OsmNightWatch.PbfParsing
{
    public static class RelationsParser
    {
        public static Dictionary<long, Relation> Parse(IEnumerable<ElementFilter> filters, PbfIndex index)
        {
            var indexFilters = new IndexedTagFilters(filters.Where(f => f.GeoType == OsmGeoType.Relation).SelectMany(f => f.Tags));
            var relationsBag = new ConcurrentBag<Relation>();
            ParallelParse(index.PbfPath, index.GetAllRelationFileOffsets().Select(o => (o, (HashSet<long>?)null)).ToList(),
                (HashSet<long>? relevantIds, byte[] readBuffer) =>
                {
                    ParseRelations(relationsBag, null, indexFilters, readBuffer);
                });
            while (true)
            {
                var dictionaryOfLoadedRelations = relationsBag.ToDictionary(r => (long)r.Id!, r => r);
                var unloadedChildren = new HashSet<long>();
                foreach (var relation in dictionaryOfLoadedRelations.Values)
                {
                    foreach (var member in relation.Members)
                    {
                        if (member.Type != OsmGeoType.Relation)
                            continue;
                        if (dictionaryOfLoadedRelations.ContainsKey(member.Id))
                            continue;
                        unloadedChildren.Add(member.Id);
                    }
                }
                if (unloadedChildren.Count == 0)
                {
                    return dictionaryOfLoadedRelations;
                }
                ParallelParse(index.PbfPath, index.CaclulateFileOffsets(unloadedChildren, OsmGeoType.Relation),
                    (HashSet<long>? relevantIds, byte[] readBuffer) =>
                    {
                        ParseRelations(relationsBag, relevantIds, null, readBuffer);
                    });
            }
        }

        public static Dictionary<long, Relation> LoadRelations(HashSet<long> relationsToLoad, PbfIndex index)
        {
            var fileOffsets = index.CaclulateFileOffsets(relationsToLoad, OsmGeoType.Way);
            var relationsBag = new ConcurrentBag<Relation>();
            ParallelParse(index.PbfPath, fileOffsets, (HashSet<long>? relevantIds, byte[] readBuffer) =>
            {
                ParseRelations(relationsBag, relevantIds, null, readBuffer);
            });

            return relationsBag.ToDictionary(r => (long)r.Id!, r => r);
        }

        public static void ParseRelations(ConcurrentBag<Relation> relationsBag, HashSet<long>? relationsToLoad, IndexedTagFilters? tagFilters, byte[] readBuffer)
        {
            ReadOnlySpan<byte> dataSpan = readBuffer;
            Decompress(ref dataSpan, out var uncompressbuffer, out var uncompressedSpan, out var uncompressedSize);
            var stringTableSize = BinSerialize.ReadProtoByteArraySize(ref uncompressedSpan).size;
            var stringTableTargetLength = uncompressedSpan.Length - stringTableSize;
            var utf8ToIdMappings = new Dictionary<Memory<byte>, int>();
            var stringSpans = new List<Memory<byte>>();
            while (uncompressedSpan.Length > stringTableTargetLength)
            {
                var (index, size) = BinSerialize.ReadProtoByteArraySize(ref uncompressedSpan);
                if (tagFilters != null && tagFilters.StringLengths.TryGetValue(size, out var utf8StringList))
                {
                    foreach (var utf8String in utf8StringList)
                    {
                        if (uncompressedSpan.Slice(0, size).SequenceEqual(utf8String.Span))
                        {
                            utf8ToIdMappings.Add(utf8String, stringSpans.Count);
                        }
                    }
                }
                stringSpans.Add(new Memory<byte>(uncompressbuffer, (int)uncompressedSize - uncompressedSpan.Length, size));
                uncompressedSpan = uncompressedSpan.Slice(size);
            }

            var stringIdFilters = new Dictionary<int, HashSet<int>?>();
            if (tagFilters != null)
            {
                foreach (var item in tagFilters.Utf8RelationsTagsFilter)
                {
                    if (item.TagValues.Count == 0)
                    {
                        stringIdFilters.Add(utf8ToIdMappings[item.TagKey], null);
                    }
                    else
                    {
                        var hashset = new HashSet<int>();
                        foreach (var tagValue in item.TagValues)
                        {
                            hashset.Add(utf8ToIdMappings[tagValue]);
                        }
                        stringIdFilters.Add(utf8ToIdMappings[item.TagKey], hashset);
                    }
                }
            }

            var tagKeys = new List<int>();
            var tagValues = new List<int>();
            var roles = new List<int>();
            var membersIds = new List<long>();
            var memberTypes = new List<OsmGeoType>();
            var expectedValues = new Dictionary<int, HashSet<int>>();
            while (uncompressedSpan.Length > 0)
            {
                var primitivegroupSize = BinSerialize.ReadProtoByteArraySize(ref uncompressedSpan).size;
                var expectedLengthAtEndOfPrimitiveGroup = uncompressedSpan.Length - primitivegroupSize;
                while (uncompressedSpan.Length > expectedLengthAtEndOfPrimitiveGroup)
                {
                    tagKeys.Clear();
                    tagValues.Clear();
                    roles.Clear();
                    membersIds.Clear();
                    memberTypes.Clear();
                    expectedValues.Clear();

                    var (index, primitiveSize) = BinSerialize.ReadProtoByteArraySize(ref uncompressedSpan);
                    var expectedLengthAtEndOfPrimitive = uncompressedSpan.Length - primitiveSize;
                    if (index != 4)
                    {
                        throw new Exception();//Only expecting Relations here
                    }
                    BinSerialize.EnsureProtoIndexAndType(ref uncompressedSpan, 1, 0);
                    var relationId = BinSerialize.ReadPackedLong(ref uncompressedSpan);
                    bool accepted = false;
                    if (relationsToLoad != null)
                    {
                        if (relationsToLoad.Contains(relationId))
                        {
                            accepted = true;
                        }
                        else
                        {
                            uncompressedSpan = uncompressedSpan.Slice(uncompressedSpan.Length - expectedLengthAtEndOfPrimitive);
                            continue;
                        }
                    }

                    while (uncompressedSpan.Length > expectedLengthAtEndOfPrimitive)
                    {
                        var (innerIndex, type) = BinSerialize.ReadProtoIndexAndType(ref uncompressedSpan);
                        switch (innerIndex)
                        {
                            case 2://Tag keys
                                var sizeOfKeys = BinSerialize.ReadPackedUnsignedInteger(ref uncompressedSpan);
                                var expectedLengthAtEndOfKeys = uncompressedSpan.Length - sizeOfKeys;
                                while (uncompressedSpan.Length > expectedLengthAtEndOfKeys)
                                {
                                    int currentTagKeyId = BinSerialize.ReadPackedInt(ref uncompressedSpan);
                                    tagKeys.Add(currentTagKeyId);
                                    if (!accepted && stringIdFilters.TryGetValue(currentTagKeyId, out var hashset))
                                    {
                                        if (hashset == null)
                                        {
                                            accepted = true;
                                        }
                                        else
                                        {
                                            expectedValues.Add(tagKeys.Count, hashset);
                                        }
                                    }
                                }
                                // accepted would be true if we had matching key without value filter
                                // expectedValues would not be null if we had matching key, with value filters
                                // if none of this is true, there is no chance to have a match...
                                if (!accepted && expectedValues.Count == 0)
                                {
                                    // Skip to next relation
                                    uncompressedSpan = uncompressedSpan.Slice(uncompressedSpan.Length - expectedLengthAtEndOfPrimitive);
                                    continue;
                                }
                                break;
                            case 3://Tag values
                                var sizeOfValues = BinSerialize.ReadPackedUnsignedInteger(ref uncompressedSpan);
                                var expectedLengthAtEndOfValues = uncompressedSpan.Length - sizeOfValues;
                                while (uncompressedSpan.Length > expectedLengthAtEndOfValues)
                                {
                                    int currentTagValueId = BinSerialize.ReadPackedInt(ref uncompressedSpan);
                                    tagValues.Add(currentTagValueId);
                                    if (!accepted && expectedValues.TryGetValue(tagValues.Count, out var acceptableValues))
                                    {
                                        if (acceptableValues.Contains(currentTagValueId))
                                        {
                                            accepted = true;
                                        }
                                    }
                                }
                                if (!accepted)
                                {
                                    // Skip to next relation
                                    uncompressedSpan = uncompressedSpan.Slice(uncompressedSpan.Length - expectedLengthAtEndOfPrimitive);
                                    continue;
                                }
                                break;
                            case 4://Info
                                var sizeOfInfo = BinSerialize.ReadPackedInt(ref uncompressedSpan);
                                uncompressedSpan = uncompressedSpan.Slice(sizeOfInfo);
                                break;
                            case 8://roles_sid
                                var sizeOfRoles = BinSerialize.ReadPackedUnsignedInteger(ref uncompressedSpan);
                                var expectedLengthAtEndOfRoles = uncompressedSpan.Length - sizeOfRoles;
                                while (uncompressedSpan.Length > expectedLengthAtEndOfRoles)
                                {
                                    int role_sid = BinSerialize.ReadPackedInt(ref uncompressedSpan);
                                    roles.Add(role_sid);
                                }
                                break;
                            case 9://memids
                                var sizeOfMemids = BinSerialize.ReadPackedUnsignedInteger(ref uncompressedSpan);
                                var expectedLengthAtEndOfMemids = uncompressedSpan.Length - sizeOfMemids;
                                long memId = 0;
                                while (uncompressedSpan.Length > expectedLengthAtEndOfMemids)
                                {
                                    memId += BinSerialize.ReadZigZagLong(ref uncompressedSpan);
                                    membersIds.Add(memId);
                                }
                                break;
                            case 10://types
                                var sizeOfTypes = BinSerialize.ReadPackedUnsignedInteger(ref uncompressedSpan);
                                var expectedLengthAtEndOfTypes = uncompressedSpan.Length - sizeOfTypes;
                                while (uncompressedSpan.Length > expectedLengthAtEndOfTypes)
                                {
                                    var memberType = (OsmGeoType)BinSerialize.ReadPackedInt(ref uncompressedSpan);
                                    memberTypes.Add(memberType);
                                }
                                break;
                            default:
                                throw new Exception();
                        }
                    }
                    if (accepted)
                    {
                        var members = new RelationMember[membersIds.Count];
                        for (int i = 0; i < members.Length; i++)
                        {
                            members[i] = new RelationMember(membersIds[i], Encoding.UTF8.GetString(stringSpans[roles[i]].Span), memberTypes[i]);
                        }
                        var tags = new TagsCollection(tagKeys.Count);
                        for (int i = 0; i < tagKeys.Count; i++)
                        {
                            tags.Add(new Tag(Encoding.UTF8.GetString(stringSpans[tagKeys[i]].Span), Encoding.UTF8.GetString(stringSpans[tagValues[i]].Span)));
                        }
                        relationsBag.Add(new Relation()
                        {
                            Id = (long)relationId,
                            Members = members,
                            Tags = tags
                        });
                    }
                }
            }
            ArrayPool<byte>.Shared.Return(uncompressbuffer);
        }

    }
}
