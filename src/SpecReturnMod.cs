using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace lionfox_specreturn
{
    public class SpecReturnConfig
    {
        public bool enabled = true;
    }

    [ProtoContract]
    public class SpecReturnTogglePacket
    {
        // Null = "toggle". True/False = "set to this value".
        [ProtoMember(1)] public bool hasExplicit;
        [ProtoMember(2)] public bool explicitValue;
    }

    [ProtoContract]
    public class SpecReturnStatePacket
    {
        [ProtoMember(1)] public bool enabled;
        // True if the server is currently holding a remembered survival position for this player.
        [ProtoMember(2)] public bool hasSavedPos;
    }

    // Universal mod. Server is the authority for runtime state (per-player); client provides
    // three input methods (chat command, hotkey, ConfigLib visual config) that all funnel into
    // the same server-side toggle. ConfigLib's value is treated as the per-client default that
    // seeds the server's state when the player joins.
    public class SpecReturnMod : ModSystem
    {
        const string ConfigAssetPath = "lionfoxspecreturn:config/lionfoxspecreturn.json";
        const string ConfigFileName  = "lionfoxspecreturn.json";
        const string NetChannelName  = "lionfoxspecreturn";
        const string HotkeyCode      = "lionfoxspecreturn-toggle";
        const string ModdataKey      = "lionfoxspecreturn";

        SpecReturnConfig config = new SpecReturnConfig();

        // server-only
        ICoreServerAPI? sapi;
        IServerNetworkChannel? sChannel;
        readonly Dictionary<string, PlayerState> serverStates = new();

        // client-only
        ICoreClientAPI? capi;
        IClientNetworkChannel? cChannel;
        bool clientLastKnownEnabled;

        class PlayerState
        {
            public bool Enabled;
            public Vec3d? SavedPos;
            public EnumGameMode LastGameMode;
        }

        public override void Start(ICoreAPI api)
        {
            LoadConfig(api);

            api.Network.RegisterChannel(NetChannelName)
                .RegisterMessageType<SpecReturnTogglePacket>()
                .RegisterMessageType<SpecReturnStatePacket>();
        }

        // ---------- config ----------

        void LoadConfig(ICoreAPI api)
        {
            try
            {
                var asset = api.Assets.TryGet(new AssetLocation(ConfigAssetPath));
                if (asset != null)
                {
                    config = asset.ToObject<SpecReturnConfig>();
                    return;
                }
            }
            catch (Exception e)
            {
                api.Logger.Warning($"[SpecReturn] Asset config parse failed ({e.Message}); falling back to ModConfig.");
            }

            try
            {
                var loaded = api.LoadModConfig<SpecReturnConfig>(ConfigFileName);
                if (loaded == null) api.StoreModConfig(config, ConfigFileName);
                else config = loaded;
            }
            catch (Exception e)
            {
                api.Logger.Warning($"[SpecReturn] ModConfig read failed ({e.Message}); using defaults.");
            }
        }

        // ============================================================
        //  CLIENT
        // ============================================================

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            clientLastKnownEnabled = config.enabled;

            cChannel = api.Network.GetChannel(NetChannelName)
                .SetMessageHandler<SpecReturnStatePacket>(OnStateFromServer);

            api.Input.RegisterHotKey(
                HotkeyCode,
                Lang.Get("lionfoxspecreturn:hotkey-toggle"),
                GlKeys.Unknown,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(HotkeyCode, OnHotkeyPressed);

            api.ChatCommands.Create("specreturn")
                .WithDescription(Lang.Get("lionfoxspecreturn:cmd-desc"))
                .WithAlias("specret")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("state"))
                .HandleWith(OnClientCommand);
        }

        bool OnHotkeyPressed(KeyCombination _)
        {
            SendToggle(null);
            return true;
        }

        TextCommandResult OnClientCommand(TextCommandCallingArgs args)
        {
            // Client-side handler runs locally for the player who typed the command. We forward
            // it to the server so command/hotkey/configlib all share one path.
            bool? target = ParseStateWord(args[0] as string);
            SendToggle(target);
            // The server will reply with the authoritative new state and the client handler
            // prints it; no immediate success message is needed.
            return TextCommandResult.Deferred;
        }

        void SendToggle(bool? explicitValue)
        {
            if (cChannel == null) return;
            var pkt = new SpecReturnTogglePacket
            {
                hasExplicit = explicitValue.HasValue,
                explicitValue = explicitValue ?? false
            };
            cChannel.SendPacket(pkt);
        }

        void OnStateFromServer(SpecReturnStatePacket pkt)
        {
            if (capi == null) return;
            clientLastKnownEnabled = pkt.enabled;
            string msg = pkt.enabled
                ? Lang.Get("lionfoxspecreturn:msg-enabled")
                : Lang.Get("lionfoxspecreturn:msg-disabled");
            capi.ShowChatMessage(msg);
        }

        // ============================================================
        //  SERVER
        // ============================================================

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            sChannel = api.Network.GetChannel(NetChannelName)
                .SetMessageHandler<SpecReturnTogglePacket>(OnToggleFromClient);

            api.ChatCommands.Create("specreturn")
                .WithDescription(Lang.Get("lionfoxspecreturn:cmd-desc"))
                .RequiresPrivilege(Privilege.chat)
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("state"))
                .HandleWith(OnServerCommand);

            api.Event.PlayerNowPlaying       += OnPlayerNowPlaying;
            api.Event.PlayerSwitchGameMode   += OnSwitchGameMode;
            api.Event.PlayerDisconnect       += OnPlayerDisconnect;
        }

        PlayerState GetOrCreateState(IServerPlayer player)
        {
            if (!serverStates.TryGetValue(player.PlayerUID, out var s))
            {
                s = LoadPersistedState(player) ?? new PlayerState
                {
                    Enabled = config.enabled,
                    LastGameMode = player.WorldData.CurrentGameMode
                };
                serverStates[player.PlayerUID] = s;
            }
            return s;
        }

        PlayerState? LoadPersistedState(IServerPlayer player)
        {
            try
            {
                var bytes = player.WorldData.GetModdata(ModdataKey);
                if (bytes == null || bytes.Length == 0) return null;

                var tree = SerializerUtil.Deserialize<TreeAttribute>(bytes);
                var st = new PlayerState
                {
                    Enabled = tree.GetBool("enabled", config.enabled),
                    LastGameMode = player.WorldData.CurrentGameMode
                };
                if (tree.HasAttribute("savedX"))
                {
                    st.SavedPos = new Vec3d(
                        tree.GetDouble("savedX"),
                        tree.GetDouble("savedY"),
                        tree.GetDouble("savedZ"));
                }
                return st;
            }
            catch (Exception e)
            {
                sapi?.Logger.Warning($"[SpecReturn] Failed to load persisted state for {player.PlayerName}: {e.Message}");
                return null;
            }
        }

        void PersistState(IServerPlayer player, PlayerState s)
        {
            try
            {
                var tree = new TreeAttribute();
                tree.SetBool("enabled", s.Enabled);
                if (s.SavedPos != null)
                {
                    tree.SetDouble("savedX", s.SavedPos.X);
                    tree.SetDouble("savedY", s.SavedPos.Y);
                    tree.SetDouble("savedZ", s.SavedPos.Z);
                }
                player.WorldData.SetModdata(ModdataKey, SerializerUtil.Serialize(tree));
            }
            catch (Exception e)
            {
                sapi?.Logger.Warning($"[SpecReturn] Failed to persist state for {player.PlayerName}: {e.Message}");
            }
        }

        void OnPlayerNowPlaying(IServerPlayer byPlayer)
        {
            var s = GetOrCreateState(byPlayer);
            s.LastGameMode = byPlayer.WorldData.CurrentGameMode;
            SendState(byPlayer, s);
        }

        void OnPlayerDisconnect(IServerPlayer byPlayer)
        {
            if (serverStates.TryGetValue(byPlayer.PlayerUID, out var s))
            {
                PersistState(byPlayer, s);
                serverStates.Remove(byPlayer.PlayerUID);
            }
        }

        void OnSwitchGameMode(IServerPlayer byPlayer)
        {
            var s = GetOrCreateState(byPlayer);
            EnumGameMode previous = s.LastGameMode;
            EnumGameMode current  = byPlayer.WorldData.CurrentGameMode;
            s.LastGameMode = current;

            if (!s.Enabled)
            {
                PersistState(byPlayer, s);
                return;
            }

            // Survival -> Spectator : capture position, but only if we don't already have one
            // (so an extra in-spectator gamemode dance can't overwrite the original anchor).
            if (previous == EnumGameMode.Survival && current == EnumGameMode.Spectator)
            {
                if (s.SavedPos == null && byPlayer.Entity != null)
                {
                    var pos = byPlayer.Entity.Pos;
                    s.SavedPos = new Vec3d(pos.X, pos.Y, pos.Z);
                    sapi?.Logger.Notification($"[SpecReturn] {byPlayer.PlayerName}: saved survival pos at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
                    SendChatMessage(byPlayer, Lang.Get("lionfoxspecreturn:msg-saved", $"({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})"));
                }
            }
            // Spectator -> Survival : teleport back if we have an anchor.
            else if (previous == EnumGameMode.Spectator && current == EnumGameMode.Survival)
            {
                if (s.SavedPos != null && byPlayer.Entity != null)
                {
                    var target = s.SavedPos;
                    byPlayer.Entity.TeleportToDouble(target.X, target.Y, target.Z, null);
                    sapi?.Logger.Notification($"[SpecReturn] {byPlayer.PlayerName}: teleported back to ({target.X:F1}, {target.Y:F1}, {target.Z:F1}).");
                    SendChatMessage(byPlayer, Lang.Get("lionfoxspecreturn:msg-returned"));
                    s.SavedPos = null;
                }
            }
            // Spectator -> anything-but-survival : the anchor no longer applies; clear it so a
            // future survival re-entry doesn't yank them back to a stale spot.
            else if (previous == EnumGameMode.Spectator && current != EnumGameMode.Survival)
            {
                s.SavedPos = null;
            }

            PersistState(byPlayer, s);
        }

        void OnToggleFromClient(IServerPlayer fromPlayer, SpecReturnTogglePacket pkt)
        {
            var s = GetOrCreateState(fromPlayer);
            bool target = pkt.hasExplicit ? pkt.explicitValue : !s.Enabled;
            ApplyEnabled(fromPlayer, s, target);
        }

        TextCommandResult OnServerCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command must be run by a player.");

            var s = GetOrCreateState(player);
            bool? parsed = ParseStateWord(args[0] as string);
            bool target = parsed ?? !s.Enabled;

            ApplyEnabled(player, s, target);
            return TextCommandResult.Success(target
                ? Lang.Get("lionfoxspecreturn:msg-status-on")
                : Lang.Get("lionfoxspecreturn:msg-status-off"));
        }

        void ApplyEnabled(IServerPlayer player, PlayerState s, bool target)
        {
            if (s.Enabled == target)
            {
                // No change — still echo current state so the user gets visible feedback.
                SendState(player, s);
                return;
            }

            s.Enabled = target;
            if (!target) s.SavedPos = null;
            PersistState(player, s);
            SendState(player, s);
            sapi?.Logger.Notification($"[SpecReturn] {player.PlayerName}: enabled={target}.");
        }

        void SendState(IServerPlayer player, PlayerState s)
        {
            sChannel?.SendPacket(new SpecReturnStatePacket
            {
                enabled = s.Enabled,
                hasSavedPos = s.SavedPos != null
            }, player);
        }

        void SendChatMessage(IServerPlayer player, string msg)
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
        }

        // ---------- shared ----------

        static bool? ParseStateWord(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                case "yes":
                case "enable":
                case "enabled":
                    return true;
                case "off":
                case "false":
                case "0":
                case "no":
                case "disable":
                case "disabled":
                    return false;
                case "toggle":
                    return null;
            }
            return null;
        }
    }
}
