using System;
using System.Collections.Generic;
using Perch;
using Xunit;

namespace Perch.Tests;

// Rates below are the real us-central1 figures read back from Google's catalog on
// 2026-07-16, so the arithmetic here is the arithmetic the app does.
public class CloudPriceCatalogTests
{
    private const double Core = 0.031611;   // A2 Instance Core, per vCPU-hour
    private const double Ram  = 0.004237;   // A2 Instance Ram, per GiB-hour
    private const double Gpu  = 3.928080;   // A100 80GB, on-demand, per GPU-hour
    private const double GpuDws = 1.846198; // ...the same card under DWS Flex-Start

    private static CloudPriceCatalog Seeded()
    {
        var c = new CloudPriceCatalog();
        c.SeedRows(new[]
        {
            new SkuRow("A2 Instance Core running in Americas", new[] { "us-central1" }, "CPU", Core),
            new SkuRow("A2 Instance Ram running in Americas",  new[] { "us-central1" }, "RAM", Ram),
            new SkuRow("Nvidia Tesla A100 80GB GPU running in Americas", new[] { "us-central1" }, "GPU", Gpu),
            new SkuRow("Nvidia Tesla A100 80GB GPU attached to DWS Defined Duration VMs running in Americas",
                       new[] { "us-central1" }, "GPU", GpuDws),
            // The 40GB card, deliberately present: its description is a prefix of
            // the 80GB one, which is exactly the trap the matcher has to avoid.
            new SkuRow("Nvidia Tesla A100 GPU running in Americas", new[] { "us-central1" }, "GPU", 2.933750),
            new SkuRow("Spot Preemptible A2 Instance Core running in Americas", new[] { "us-central1" }, "CPU", 0.012),
        });
        return c;
    }

    /// a2-ultragpu-4g: 48 vCPU, 680 GiB, 4× A100-80GB.
    private static MachineSpec UltraGpu4g() =>
        new(48, 680, new List<(string, int)> { ("nvidia-a100-80gb", 4) });

    [Fact]
    public void Prices_a_machine_from_its_component_skus()
    {
        // There is no "a2-ultragpu-4g" SKU anywhere in the catalog — the price is
        // the SUM of cores + RAM + cards. This is the whole reason the catalog can
        // scale where a per-shape table can't.
        var got = Seeded().PerHour("a2-ultragpu-4g", "us-central1", PriceVariant.Standard, UltraGpu4g());
        Assert.NotNull(got);
        Assert.Equal(48 * Core + 680 * Ram + 4 * Gpu, got!.Value, precision: 4);
        Assert.Equal(20.1108, got.Value, precision: 4);   // vs 20.2752 in the static table
    }

    [Fact]
    public void A_flex_start_box_pays_the_dws_card_rate_not_on_demand()
    {
        // The bug this whole thing exists to kill: the static table has ONE number
        // per shape, so it billed a DWS A100 box at on-demand — ~72% too high.
        var got = Seeded().PerHour("a2-ultragpu-4g", "us-central1", PriceVariant.FlexStart, UltraGpu4g());
        Assert.NotNull(got);
        Assert.Equal(11.7833, got!.Value, precision: 4);
        Assert.True(got.Value < 20.1108 * 0.65, "a DWS box must land far under on-demand");
    }

    [Fact]
    public void Cores_fall_back_to_on_demand_when_the_variant_has_no_sku_of_its_own()
    {
        // Google publishes a DWS price for the CARD but not for the cores/RAM
        // beside it. Dropping the whole machine over that would be worse than
        // pricing its CPU at on-demand, so the fixture has no DWS core row and the
        // total must still resolve.
        var got = Seeded().PerHour("a2-ultragpu-4g", "us-central1", PriceVariant.FlexStart, UltraGpu4g());
        Assert.Equal(48 * Core + 680 * Ram + 4 * GpuDws, got!.Value, precision: 4);
    }

    [Theory]
    // our card                 sku description                                            match?
    [InlineData("nvidia-a100-80gb", "nvidia tesla a100 80gb gpu running in americas",      true)]
    [InlineData("nvidia-a100-80gb", "nvidia tesla a100 gpu running in americas",           false)]
    [InlineData("nvidia-tesla-a100", "nvidia tesla a100 gpu running in americas",          true)]
    [InlineData("nvidia-tesla-a100", "nvidia tesla a100 80gb gpu running in americas",     false)]
    [InlineData("nvidia-tesla-t4",  "nvidia tesla t4 gpu running in americas",             true)]
    [InlineData("nvidia-tesla-t4",  "nvidia tesla a100 gpu running in americas",           false)]
    public void The_size_token_keeps_the_80gb_card_apart_from_the_40gb_one(
        string acceleratorType, string desc, bool expected)
    {
        // "a100" is a substring of the "A100 80GB" row. Without the size check the
        // 40GB card silently prices as the 80GB one — same-looking row, ~30% out.
        Assert.Equal(expected, CloudPriceCatalog.GpuMatches(desc, acceleratorType));
    }

    [Fact]
    public void An_unpriceable_card_yields_null_rather_than_a_wrong_number()
    {
        // A partial answer is worse than none: a GPU box priced without its GPU
        // reads as cheap. Null sends the caller to the static table instead.
        var spec = new MachineSpec(48, 680, new List<(string, int)> { ("nvidia-h100-80gb", 8) });
        Assert.Null(Seeded().PerHour("a2-ultragpu-4g", "us-central1", PriceVariant.Standard, spec));
    }

    [Fact]
    public void A_region_we_have_no_sku_for_yields_null()
        => Assert.Null(Seeded().PerHour("a2-ultragpu-4g", "europe-west4", PriceVariant.Standard, UltraGpu4g()));

    [Fact]
    public void An_unknown_family_yields_null()
        => Assert.Null(Seeded().PerHour("c4-hypernova-99", "us-central1", PriceVariant.Standard,
            new MachineSpec(8, 32, Array.Empty<(string, int)>())));

    [Theory]
    [InlineData("FLEX_START", "FlexStart")]
    [InlineData("SPOT", "Spot")]
    [InlineData("STANDARD", "Standard")]
    [InlineData(null, "Standard")]
    [InlineData("", "Standard")]
    [InlineData("something-new", "Standard")]   // unknown model must never guess a discount
    public void Provisioning_model_picks_the_variant(string? model, string expected)
        => Assert.Equal(expected, CloudPriceCatalog.VariantOf(model).ToString());

    [Fact]
    public void Family_comes_from_the_machine_types_first_segment()
    {
        // This is what makes it scale: a2-ultragpu-16g would price itself tomorrow
        // with no code change, because only the FAMILY is mapped, not the shape.
        Assert.Equal("a2", CloudPriceCatalog.FamilyOf("a2-ultragpu-4g"));
        Assert.Equal("n2d", CloudPriceCatalog.FamilyOf("n2d-standard-16"));
        Assert.Equal("", CloudPriceCatalog.FamilyOf(null));
    }

    [Fact]
    public void Distill_keeps_priceable_rows_and_drops_commitments()
    {
        // Commitments and reservations can't price a running VM, and dropping them
        // is most of why the disk cache is ~1.4MB instead of ~24MB.
        const string json = """
        [
          { "description":"A2 Instance Core running in Americas",
            "category":{"resourceGroup":"CPU"}, "serviceRegions":["us-central1"],
            "pricingInfo":[{"pricingExpression":{"usageUnit":"h","tieredRates":[
              {"unitPrice":{"units":"0","nanos":31611000}}]}}] },
          { "description":"Commitment v1: A2 Cpu in Americas for 1 Year",
            "category":{"resourceGroup":"CPU"}, "serviceRegions":["us-central1"],
            "pricingInfo":[{"pricingExpression":{"usageUnit":"h","tieredRates":[
              {"unitPrice":{"units":"0","nanos":10000000}}]}}] },
          { "description":"Network Internet Egress from Americas",
            "category":{"resourceGroup":"PremiumInternetEgress"}, "serviceRegions":["us-central1"],
            "pricingInfo":[{"pricingExpression":{"usageUnit":"GiBy","tieredRates":[
              {"unitPrice":{"units":"0","nanos":120000000}}]}}] }
        ]
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var rows = CloudPriceCatalog.Distill(doc.RootElement);

        var row = Assert.Single(rows);
        Assert.Equal("A2 Instance Core running in Americas", row.Desc);
        Assert.Equal("CPU", row.Group);
        Assert.Equal(0.031611, row.Price, precision: 6);
    }
}
