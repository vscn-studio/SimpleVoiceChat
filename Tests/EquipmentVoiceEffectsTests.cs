using System.Reflection;
using Newtonsoft.Json;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Common;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class EquipmentVoiceEffectsTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void ExplicitEmptyRulesSurviveMigrationAndRepeatedConfigReloads(int version)
    {
        string json = $$"""{"ConfigVersion":{{version}},"EquipmentVoiceEffectRules":[]}""";
        for (int reload = 0; reload < 3; reload++)
        {
            SimpleVoiceChatServerConfig config = JsonConvert.DeserializeObject<SimpleVoiceChatServerConfig>(json)!;
            config.Normalize();
            Assert.Empty(config.EquipmentVoiceEffectRules);
            Assert.True(config.EnableEnvironmentalVoiceEffects);
            json = JsonConvert.SerializeObject(config);
        }
    }

    [Fact]
    public void CustomRulesReplaceDefaultsWithoutGrowingOnReload()
    {
        string json = """
            {"ConfigVersion":12,"EquipmentVoiceEffectRules":[
                {"Slot":"Face","ItemCodePattern":"custom:respirator-*","Effect":"Mask"}
            ]}
            """;
        for (int reload = 0; reload < 3; reload++)
        {
            SimpleVoiceChatServerConfig config = JsonConvert.DeserializeObject<SimpleVoiceChatServerConfig>(json)!;
            config.Normalize();
            VoiceEquipmentEffectRule rule = Assert.Single(config.EquipmentVoiceEffectRules);
            Assert.Equal("custom:respirator-*", rule.ItemCodePattern);
            Assert.Equal(VoiceEquipmentSlot.Face, rule.Slot);
            Assert.Equal(VoiceEquipmentVoiceEffect.Mask, rule.Effect);
            json = JsonConvert.SerializeObject(config);
        }
    }

    [Fact]
    public void OmittedRulesStillReceiveDefaults()
    {
        SimpleVoiceChatServerConfig config = JsonConvert.DeserializeObject<SimpleVoiceChatServerConfig>("{}")!;
        config.Normalize();
        Assert.Equal(2, config.EquipmentVoiceEffectRules.Count);
    }

    [Fact]
    public void WornHelmetIsResolvedWithoutEnumeratingUninitializedInventories()
    {
        InventoryGeneric equipment = new(1, "character-test", null,
            (_, inventory) => new ItemSlotCharacter(EnumCharacterDressType.ArmorHead, inventory));
        equipment[0].Itemstack = new ItemStack(new Item { Code = new AssetLocation("game:armor-head-copper") });
        IPlayerInventoryManager manager = CreateManager(equipment);
        List<ItemSlotCharacter> scratch = new();
        SimpleVoiceChatServerConfig config = new();

        Assert.Equal(VoiceSourceEffectFlags.Helmet,
            ServerVoiceController.ResolveEquipmentEffects(manager, config.EquipmentVoiceEffectRules, scratch));

        // Removing equipment must clear the previous frame's scratch state.
        equipment[0].Itemstack = null;
        Assert.Equal(VoiceSourceEffectFlags.None,
            ServerVoiceController.ResolveEquipmentEffects(manager, config.EquipmentVoiceEffectRules, scratch));
        Assert.Empty(scratch);
    }

    [Fact]
    public void WornMaskAndHelmetBothAffectVoice()
    {
        InventoryGeneric equipment = new(2, "character-test", null,
            (index, inventory) => new ItemSlotCharacter(
                index == 0 ? EnumCharacterDressType.ArmorHead : EnumCharacterDressType.Face,
                inventory));
        equipment[0].Itemstack = new ItemStack(new Item { Code = new AssetLocation("game:armor-head-copper") });
        equipment[1].Itemstack = new ItemStack(new Item { Code = new AssetLocation("game:clothes-face-leather-mask") });

        VoiceSourceEffectFlags effects = ServerVoiceController.ResolveEquipmentEffects(
            CreateManager(equipment), new SimpleVoiceChatServerConfig().EquipmentVoiceEffectRules, new());

        Assert.Equal(VoiceSourceEffectFlags.Helmet | VoiceSourceEffectFlags.Mask, effects);
    }

    [Fact]
    public void MissingEquipmentInventoryDoesNotInterruptVoice()
    {
        Assert.Equal(VoiceSourceEffectFlags.None, ServerVoiceController.ResolveEquipmentEffects(
            CreateManager(null), new SimpleVoiceChatServerConfig().EquipmentVoiceEffectRules, new()));
    }

    [Fact]
    public void DisabledEquipmentRulesDoNotAccessInventories()
    {
        IPlayerInventoryManager manager = DispatchProxy.Create<IPlayerInventoryManager, InventoryManagerProxy>();
        ((InventoryManagerProxy)manager).RejectEquipmentAccess = true;
        Assert.Equal(VoiceSourceEffectFlags.None,
            ServerVoiceController.ResolveEquipmentEffects(manager, Array.Empty<VoiceEquipmentEffectRule>(), new()));
    }

    private static IPlayerInventoryManager CreateManager(IInventory? equipment)
    {
        IPlayerInventoryManager manager = DispatchProxy.Create<IPlayerInventoryManager, InventoryManagerProxy>();
        ((InventoryManagerProxy)manager).Equipment = equipment;
        return manager;
    }

    public class InventoryManagerProxy : DispatchProxy
    {
        public IInventory? Equipment { get; set; }
        public bool RejectEquipmentAccess { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetOwnInventory" && !RejectEquipmentAccess)
            {
                Assert.Equal("character", Assert.Single(args!));
                return Equipment;
            }
            // Accessing unrelated inventories can reach an uninitialized creative tab.
            throw new InvalidOperationException($"Unexpected inventory access: {targetMethod?.Name}");
        }
    }
}
