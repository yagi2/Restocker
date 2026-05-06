using System.Collections.Generic;
using System.Linq;
using Restocker.Data;
using Restocker.Plan;

namespace Restocker.Tests;

public class PlannerTests
{
    private static RetainerSnapshot MakeSnapshot(int alreadyListed, int ownedQty, uint itemId = 100, bool isHq = false)
    {
        var snap = new RetainerSnapshot
        {
            CharacterContentId = 0xABCDEF,
            CharacterName = "Test",
            RetainerId = 1,
            RetainerName = "R1",
        };
        for (var i = 0; i < alreadyListed; i++)
        {
            snap.Listings.Add(new ListingEntry { ItemId = itemId, IsHQ = isHq, ListingIndex = i, Quantity = 1, UnitPrice = 100 });
        }
        if (ownedQty > 0)
        {
            snap.Inventory.Add(new InventoryEntry { ItemId = itemId, IsHQ = isHq, Quantity = ownedQty, MaxStackPerListing = 99 });
        }
        return snap;
    }

    [Fact]
    public void PlanNewListings_default_fills_max_stacks_then_remainder()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 250);
        var plan = Planner.PlanNewListings(new[] { snap }, itemId: 100, isHQ: false, unitPrice: 1000, maxStackPerListing: 99);
        Assert.Equal(3, plan.Count);
        Assert.Equal(99, plan[0].Quantity);
        Assert.Equal(99, plan[1].Quantity);
        Assert.Equal(52, plan[2].Quantity);
        Assert.All(plan, a => Assert.Equal(1000, a.UnitPrice));
    }

    [Fact]
    public void PlanNewListings_listingsCap_zero_yields_empty()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 250);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99, listingsCap: 0);
        Assert.Empty(plan);
    }

    [Fact]
    public void PlanNewListings_perListingQty_zero_yields_empty()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 250);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99, perListingQty: 0);
        Assert.Empty(plan);
    }

    [Fact]
    public void PlanNewListings_listingsCap_suppresses_remainder()
    {
        // 250 owned, qtyPer=99, cap=2 → exactly 99 + 99, the 52 remainder is dropped.
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 250);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99, listingsCap: 2);
        Assert.Equal(2, plan.Count);
        Assert.Equal(99, plan[0].Quantity);
        Assert.Equal(99, plan[1].Quantity);
    }

    [Fact]
    public void PlanNewListings_perListingQty_overrides_max_stack()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 100);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, maxStackPerListing: 99, perListingQty: 50);
        Assert.Equal(2, plan.Count);
        Assert.Equal(50, plan[0].Quantity);
        Assert.Equal(50, plan[1].Quantity);
    }

    [Fact]
    public void PlanNewListings_perListingQty_clamped_to_max_stack()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 100);
        // Asked for 200 per listing but max stack is 99 → planner clamps to 99.
        // owned 100 = 99 (one stack) + 1 (remainder). With no listingsCap the remainder is included.
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, maxStackPerListing: 99, perListingQty: 200);
        Assert.Equal(2, plan.Count);
        Assert.Equal(99, plan[0].Quantity);
        Assert.Equal(1, plan[1].Quantity);
    }

    [Fact]
    public void PlanNewListings_caps_at_free_listing_slots()
    {
        var snap = MakeSnapshot(alreadyListed: 18, ownedQty: 990);
        // Only 2 free slots (20 - 18). Even with default behaviour, plan stops at 2 stacks.
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99);
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void PlanNewListings_no_inventory_yields_empty()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 0);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99);
        Assert.Empty(plan);
    }

    [Fact]
    public void PlanNewListings_all_slots_full_yields_empty()
    {
        var snap = MakeSnapshot(alreadyListed: 20, ownedQty: 100);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99);
        Assert.Empty(plan);
    }

    [Fact]
    public void PlanNewListings_sourceKey_defaults_to_retainer()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 50);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99);
        Assert.Equal(snap.Key, plan[0].SourceKey);
        Assert.Equal(snap.Key, plan[0].RetainerKey);
    }

    [Fact]
    public void PlanNewListings_sourceKey_override_used_for_char_bag()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 50);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99, sourceKeyOverride: "char.42.bag");
        Assert.Equal("char.42.bag", plan[0].SourceKey);
        Assert.Equal(snap.Key, plan[0].RetainerKey);
    }

    [Fact]
    public void PlanFromInventoryList_uses_external_inventory_count()
    {
        var snap = MakeSnapshot(alreadyListed: 0, ownedQty: 0);
        // External (char bag) supply of 250 of the same item.
        var inventory = new List<InventoryEntry>
        {
            new() { ItemId = 100, IsHQ = false, Quantity = 250, MaxStackPerListing = 99 },
        };
        var plan = Planner.PlanFromInventoryList(inventory, "char.X.bag", snap, 100, false, 1000, 99);
        Assert.Equal(3, plan.Count);
        Assert.Equal(99, plan[0].Quantity);
        Assert.Equal(99, plan[1].Quantity);
        Assert.Equal(52, plan[2].Quantity);
        Assert.All(plan, a => Assert.Equal("char.X.bag", a.SourceKey));
    }

    [Fact]
    public void Overflow_reports_qty_over_listing_capacity()
    {
        var snap = MakeSnapshot(alreadyListed: 18, ownedQty: 990);
        var plan = Planner.PlanNewListings(new[] { snap }, 100, false, 1000, 99);
        // Owned 990, but only 2 slots × 99 = 198 listed → 792 overflow.
        var overflow = Planner.Overflow(new[] { snap }, plan, 100, false);
        Assert.Equal(792, overflow);
    }
}
