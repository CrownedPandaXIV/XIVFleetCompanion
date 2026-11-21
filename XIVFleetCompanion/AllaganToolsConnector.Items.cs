using System;
using System.Collections.Generic;

namespace XIVFleetCompanion
{
    // Reading Allagan Tools' answers, apart from asking for them (AllaganToolsConnector.cs): no Dalamud here, so it is
    // tested on its own (tests/FleetWriterTests).
    public partial class AllaganToolsConnector
    {
        public class ParsedItem
        {
            public uint Container;
            public uint Slot;
            public uint ItemId;
            public uint Quantity;
            public ulong RetainerId;
            public uint SortedContainer;
            public int SortedSlotIndex;
            public uint[] GearSetIds = Array.Empty<uint>();
            // Only for an item up for sale on a retainer: its price per item.
            public long MarketPrice;
            public bool Hq;
        }

        /// <summary>
        /// What Allagan Tools answered for one character, retainer or Free Company id: its slots (empty when it has never
        /// seen that source), or Problem when it couldn't be asked or answered in a shape this version doesn't know (an
        /// Allagan Tools update that changed its connection). A source with a problem must keep its stored items: an
        /// answer read wrongly would otherwise look like an emptied inventory.
        /// </summary>
        public sealed record Answer(List<ParsedItem> Items, string? Problem);

        /// <summary>Allagan Tools' answer, a set of numbers per slot, read by position (see ToNumeric in its source).</summary>
        public static Answer Parse(IEnumerable<ulong[]>? raw)
        {
            var result = new List<ParsedItem>();
            if (raw == null) return new(result, null);
            foreach (var item in raw)
            {
                // 25 numbers (24 before Allagan Tools added the market price), then one per gear set the item is in.
                // Fewer means the layout changed.
                if (item.Length < 24) return new(new(), $"a slot came with {item.Length} numbers, not 24 or more");
                // A slot with something in it has an item. A quantity with no item means the numbers moved.
                if (item[2] == 0 && item[3] != 0) return new(new(), "a slot had a quantity but no item");
                if (item[2] > uint.MaxValue || item[3] > uint.MaxValue || item[20] > uint.MaxValue || item[22] > int.MaxValue)
                    return new(new(), "a slot had numbers out of range");

                var gearSetIds = new uint[Math.Max(0, item.Length - 25)];
                for (var i = 0; i < gearSetIds.Length; i++) gearSetIds[i] = (uint)item[25 + i];

                result.Add(new ParsedItem
                {
                    Container = (uint)item[0],
                    Slot = (uint)item[1],
                    ItemId = (uint)item[2],
                    Quantity = (uint)item[3],
                    RetainerId = item[23],
                    SortedContainer = (uint)item[20],
                    SortedSlotIndex = (int)item[22],
                    MarketPrice = item.Length > 24 ? (long)item[24] : 0,
                    Hq = (item[6] & 1) != 0,
                    GearSetIds = gearSetIds,
                });
            }
            return new(result, null);
        }
    }
}
