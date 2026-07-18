using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetCapsuleOrderCalculatorTests
{
    [Fact]
    public void HorizontalDrag_MovesMemberToNearestLogicalSlot()
    {
        string[] order = ["one", "two", "three"];
        RectInt32[] slots =
        [
            new(0, 0, 80, 40),
            new(88, 0, 80, 40),
            new(176, 0, 80, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "one",
            new RectInt32(170, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(new[] { "two", "three", "one" }, result);
    }

    [Fact]
    public void ReversedPhysicalLayout_StillUsesPersistedLogicalSlots()
    {
        string[] order = ["one", "two", "three"];
        RectInt32[] slots =
        [
            new(176, 0, 80, 40),
            new(88, 0, 80, 40),
            new(0, 0, 80, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "one",
            new RectInt32(4, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(new[] { "two", "three", "one" }, result);
    }

    [Fact]
    public void VerticalDrag_UsesVerticalAxisOnly()
    {
        string[] order = ["one", "two", "three"];
        RectInt32[] slots =
        [
            new(20, 0, 120, 40),
            new(20, 48, 120, 40),
            new(20, 96, 120, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "three",
            new RectInt32(400, 0, 120, 40),
            SettingsService.WidgetCapsuleBarDirectionVertical);

        Assert.Equal(new[] { "three", "one", "two" }, result);
    }

    [Fact]
    public void SmallDrag_KeepsCurrentOrder()
    {
        string[] order = ["one", "two"];
        RectInt32[] slots =
        [
            new(0, 0, 80, 40),
            new(88, 0, 80, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "one",
            new RectInt32(12, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(order, result);
    }

    [Fact]
    public void RepeatedDrag_UsesStableSlotsAfterFirstReorder()
    {
        RectInt32[] slots =
        [
            new(0, 0, 80, 40),
            new(88, 0, 80, 40),
            new(176, 0, 80, 40)
        ];
        IReadOnlyList<string> first = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            ["one", "two", "three"],
            slots,
            "one",
            new RectInt32(170, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        IReadOnlyList<string> second = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            first,
            slots,
            "one",
            new RectInt32(170, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(new[] { "two", "three", "one" }, second);
    }

    [Fact]
    public void MergeGroupOrder_PreservesMembersFromOtherDisplays()
    {
        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MergeGroupOrder(
            ["left-one", "right-one", "left-two", "right-two"],
            ["left-two", "left-one"]);

        Assert.Equal(
            new[] { "left-two", "right-one", "left-one", "right-two" },
            result);
    }
}
