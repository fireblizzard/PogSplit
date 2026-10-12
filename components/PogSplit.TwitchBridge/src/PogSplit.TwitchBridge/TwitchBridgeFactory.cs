using System;

using LiveSplit.Model;
using LiveSplit.UI.Components;

using PogSplit.TwitchBridge;

[assembly: ComponentFactory(typeof(TwitchBridgeFactory))]

namespace PogSplit.TwitchBridge;

public class TwitchBridgeFactory : IComponentFactory
{
    public string ComponentName => TwitchBridgeComponent.Name;

    public string Description => "Sends your splits to the PogSplit Twitch extension so viewers can show splits, stats and comparisons.";

    public ComponentCategory Category => ComponentCategory.Other;

    public IComponent Create(LiveSplitState state)
    {
        return new TwitchBridgeComponent(state);
    }

    public string UpdateName => ComponentName;

    public string XMLURL => "";

    public string UpdateURL => "";

    public Version Version => Version.Parse("1.0.0");
}
