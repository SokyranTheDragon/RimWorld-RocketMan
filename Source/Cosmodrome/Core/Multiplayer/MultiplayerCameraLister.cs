using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.Client;
using Multiplayer.Common;
using RimWorld.Planet;
using Verse;

namespace RocketMan
{
    public static class MultiplayerCameraLister
    {
        public readonly record struct PlayerCameraData(Map CurrentMap, CellRect CameraRect, CameraZoomRange CameraZoom)
        {
            public Map CurrentMap { get; } = CurrentMap;
            public CellRect CameraRect { get; } = CameraRect;
            public CameraZoomRange CameraZoom { get; } = CameraZoom;
        }

        private static readonly Dictionary<int, PlayerCameraData> PlayerCamerasDictionary = new();

        public static IEnumerable<PlayerCameraData> PlayerCameras
        {
            get
            {
                // SP
                if (Multiplayer.Client.Multiplayer.Client == null)
                    yield return new PlayerCameraData(Find.CurrentMap, Find.CameraDriver.CurrentViewRect, Find.CameraDriver.CurrentZoom);
                // MP
                else
                {
                    foreach (var (_, value) in PlayerCamerasDictionary)
                        yield return value;
                }
            }
        }

        private static readonly Stopwatch CameraUpdateTimer = Stopwatch.StartNew();

        private static Packets ClientCameraData;
        
        private static SyncMethod SyncHandleDrawingWorld;
        private static SyncMethod SyncHandleCameraData;
        private static SyncMethod SyncClearCameraData;

        private static CellRect PrevCameraPos;
        private static CameraZoomRange PrevZoomRange;

        [Main.OnDefsLoaded]
        public static void Initialize()
        {
            var handlers = MpConnectionState.packetHandlers;

            if (handlers.ToEnumerable().All(x => x == null))
            {
                Log.Error("ROCKETMAN: MP packet handlers aren't initialized yet, injecting into MP failed");
                return;
            }

            var count = handlers.GetLength(1);
            ClientCameraData = (Packets)count;

            var extendedHandlers = new PacketHandlerInfo[(int)ConnectionStateEnum.Count, count + 1];

            // Copy the old handlers to the bigger array
            for (var state = 0; state < (int)ConnectionStateEnum.Count; state++)
            for (var packet = 0; packet < (int)Packets.Count; packet++)
                extendedHandlers[state, packet] = handlers[state, packet];

            var clientData = MethodInvoker.GetHandler(AccessTools.Method(typeof(MultiplayerCameraLister), nameof(ReceiveClientCamera)));

            for (var state = 0; state < (int)ConnectionStateEnum.Count; state++) 
                extendedHandlers[state, (int)ClientCameraData] = new PacketHandlerInfo(clientData, false);

            SyncHandleDrawingWorld = SyncMethod.Register(typeof(MultiplayerCameraLister), nameof(HandleDrawingWorld));
            SyncHandleCameraData = SyncMethod.Register(typeof(MultiplayerCameraLister), nameof(HandleCameraData));
            SyncClearCameraData = SyncMethod.Register(typeof(MultiplayerCameraLister), nameof(ClearCameraData));

            MpConnectionState.packetHandlers = extendedHandlers;
            IncreaseMaxAllowedPacketId.insertedPacketHandlers = true;
        }

        [Main.OnTickRare]
        public static void TickCamera()
        {
            // Both features disabled, no point in sending data
            if (!RocketPrefs.TimeDilation && !RocketPrefs.CorpsesRemovalEnabled)
                return;
            if (Multiplayer.Client.Multiplayer.Client == null || CameraUpdateTimer.Elapsed.Seconds < 3 || !IncreaseMaxAllowedPacketId.MultiplayerCameraPatched)
                return;

            var cameraDriver = Find.CameraDriver;
            var cameraPos = cameraDriver.CurrentViewRect;
            var zoom = cameraDriver.CurrentZoom;

            if (PrevZoomRange == zoom && PrevCameraPos == cameraPos)
                return;

            CameraUpdateTimer.Restart();
            WriteCameraData(cameraPos, zoom);
        }

        [Main.OnWorldLoaded]
        public static void CleanOnLoad()
        {
            Log.Message("ROCKETMAN: Cleaning up MP camera data");
            PlayerCamerasDictionary.Clear();
        }

        private static void ReceiveClientCamera(ByteReader reader)
        {
            if (MultiplayerServer.instance == null)
                return;

            var playerId = reader.ReadInt32();
            var isDrawingWorld = reader.ReadBool();

            if (isDrawingWorld)
            {
                SyncHandleDrawingWorld.DoSync(null, playerId);
                return;
            }

            var mapId = reader.ReadInt32();
            var (cameraRect, zoom) = ReadCameraData(reader);

            SyncHandleCameraData.DoSync(null,
                playerId, 
                Find.Maps.Find(m => m.uniqueID == mapId), 
                cameraRect.minX,
                cameraRect.maxX,
                cameraRect.minZ,
                cameraRect.maxZ,
                zoom);

            // Cleanup players who desynced/left
            var playersToCleanup = PlayerCamerasDictionary.Where(data =>
            {
                var player = Multiplayer.Client.Multiplayer.session.players.Find(x => x.id == data.Key);
                return player == null || player.status == PlayerStatus.Desynced;
            }).ToArray();

            if (playersToCleanup.Length > 0) 
                SyncClearCameraData.DoSync(null, playersToCleanup);
        }

        private static void HandleDrawingWorld(int playerId)
        {
            PlayerCamerasDictionary.Remove(playerId);
        }

        private static void HandleCameraData(int playerId, Map map, int xMin, int xMax, int zMin, int zMax, byte zoomLevel)
        {
            PlayerCamerasDictionary[playerId] = new PlayerCameraData(
                map,
                new CellRect
                {
                    minX = xMin,
                    maxX = xMax,
                    minZ = zMin,
                    maxZ = zMax,
                },
                (CameraZoomRange)zoomLevel
            );
        }

        private static void ClearCameraData(int[] playerId)
            => PlayerCamerasDictionary.RemoveAll(player => playerId.Contains(player.Key));

        private static void WriteCameraData(CellRect cameraRect, CameraZoomRange zoom)
        {
            var writer = new ByteWriter();

            // Send the player ID so we can identify the camera rects by player
            writer.WriteInt32(Multiplayer.Client.Multiplayer.session.playerId);

            // Send if the world is rendered/no map
            var worldRendered = WorldRendererUtility.WorldRenderedNow;
            writer.WriteBool(worldRendered);

            if (!worldRendered)
            {
                // Write the current map
                writer.WriteInt32(Find.CurrentMap.uniqueID);

                // Write the camera data
                WriteCameraData(writer, cameraRect, zoom);
            }

            Multiplayer.Client.Multiplayer.Client.Send(ClientCameraData, writer.ToArray(), false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteCameraData(ByteWriter writer, CellRect cameraRect, CameraZoomRange zoom)
        {
            // Write camera coordinates
            writer.WriteInt32(cameraRect.minX);
            writer.WriteInt32(cameraRect.maxX);
            writer.WriteInt32(cameraRect.minZ);
            writer.WriteInt32(cameraRect.maxZ);

            // Write current zoom level
            writer.WriteByte((byte)zoom);

            PrevCameraPos = cameraRect;
            PrevZoomRange = zoom;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (CellRect cameraRect, byte zoom) ReadCameraData(ByteReader reader)
        {
            var cameraRect = new CellRect
            {
                minX = reader.ReadInt32(),
                maxX = reader.ReadInt32(),
                minZ = reader.ReadInt32(),
                maxZ = reader.ReadInt32(),
            };
            var zoom = reader.ReadByte();

            return (cameraRect, zoom);
        }
    }
}