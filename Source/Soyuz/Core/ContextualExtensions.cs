using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Multiplayer.API;
using RimWorld;
using RocketMan;
using UnityEngine;
using Verse;

namespace Soyuz
{
    public static partial class ContextualExtensions
    {
        private static Pawn _pawnTick;
        private static bool _finilizePhase = false;

        private const int TransformationCacheSize = 2500;

        private static readonly int[] _transformationCache = new int[TransformationCacheSize];
        private static readonly Dictionary<int, int> timers = new Dictionary<int, int>();

        [Main.OnWorldLoaded]
        private static void CleanData()
        {
            Reset();
            timers.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DilationRateOnScreen(IntVec3 pos, Map map)
        {
            var lowestZoom = CameraZoomRange.Furthest;

            foreach (var cameraData in MultiplayerCameraLister.PlayerCameras)
            {
                if (cameraData.CameraZoom >= lowestZoom)
                    continue;

                if (cameraData.CurrentMap != map)
                    continue;

                if (!cameraData.CameraRect.Contains(pos))
                    continue;

                lowestZoom = cameraData.CameraZoom;
            }

            return lowestZoom switch
            {
                CameraZoomRange.Closest => 2,
                CameraZoomRange.Close => 3,
                CameraZoomRange.Middle => (int)(25 * Context.Settings.dilationFactorOnscreen),
                CameraZoomRange.Far => (int)(30 * Context.Settings.dilationFactorOnscreen),
                CameraZoomRange.Furthest => (int)(35 * Context.Settings.dilationFactorOnscreen),
                _ => 1
            };
        }

        private static int DilationRateOffScreen
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)(45 * Context.Settings.dilationFactorOffscreen); }
        }

        public static Pawn Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pawnTick;
        }

        [Main.OnInitialization]
        public static void Initialize()
        {            
            for (int i = 0; i < _transformationCache.Length; i++)
                _transformationCache[i] = (int)Mathf.Max(Mathf.RoundToInt(i / 30) * 30, 30);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RoundTransform(int interval)
        {
            if (interval >= TransformationCacheSize)
                return (int)Mathf.Max(Mathf.RoundToInt(interval / 30) * 30, 30);
            return _transformationCache[interval];
        }

        private static Stopwatch _stopwatch = new Stopwatch();

        public static void BeginTick(this Pawn pawn)
        {
            Context.PartiallyDilatedContext = false;

            Context.CurRaceSettings = pawn.GetRaceSettings();
            Context.CurJobSettings = pawn.GetCurJobSettings();

            _stopwatch.Restart();
            _finilizePhase = false;
            _pawnTick = pawn;

            if (!RocketPrefs.Enabled || 
                !RocketPrefs.TimeDilation || 
                Context.CurJobSettings == null || 
                Context.CurJobSettings.throttleMode == JobThrottleMode.None || 
                !IncreaseMaxAllowedPacketId.MultiplayerCameraPatched)
            {
                _throttledPawn = pawn;
                _isBeingThrottled = false;
            }

            if (!pawn.IsBeingThrottled())
            {
                if (pawn.GetTimeDelta() > 1 && Context.CurJobSettings != null)
                {
                    _throttledPawn = pawn;
                    _isBeingThrottled = true;
                    _finilizePhase = true;
                }
                else
                {
                    pawn.UpdateTimers();
                }
            }
        }

        public static void EndTick(this Pawn pawn)
        {
            _pawnTick = null;
            _stopwatch.Stop();

            try
            {
                if (Prefs.DevMode && 
                    RocketDebugPrefs.Debug && 
                    RocketDebugPrefs.LogData && 
                    RocketEnvironmentInfo.IsDevEnv && 
                    Time.frameCount - RocketStates.LastFrame < 60 && 
                    pawn == Context.ProfiledPawn)
                {
                    UpdateModels(pawn);
                }
            }
            finally
            {
                Reset();
            }
        }

        public static void Reset()
        {
            _finilizePhase = false;
            _pawnScreen = null;
            _pawnTick = null;

            Context.CurJobSettings = null;
            Context.CurRaceSettings = null;
        }

        public static bool IsCustomTickInterval(this Thing thing, int interval)
        {
            if (Current == thing && Current.IsBeingThrottled())
                return IsCustomTickInterval_newtemp(thing, interval);

            return (thing.thingIDNumber + GenTicks.TicksGame) % interval == 0;
        }

        public static bool IsCustomTickInterval_newtemp(Thing thing, int interval)
        {
            if (Current.IsBeingThrottled())
            {
                if (WorldPawnsTicker.isActive)
                    return (thing.thingIDNumber + GenTicks.TicksGame) % interval == 0;
                //if (WorldPawnsTicker.isActive)
                //    return WorldPawnsTicker.IsCustomWorldTickInterval(thing, interval);
                return (thing.thingIDNumber + GenTicks.TicksGame) % RoundTransform(interval) == 0;
            }

            return (thing.thingIDNumber + GenTicks.TicksGame) % interval == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetTimeDelta(this Thing thing)
        {
            int tick = GenTicks.TicksGame;
            if (timers.TryGetValue(thing?.thingIDNumber ?? -1, out int t0))
                return Mathf.Clamp(tick - t0, 1, 60);
            return 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UpdateTimers(this Pawn pawn)
        {
            timers[pawn.thingIDNumber] = GenTicks.TicksGame;
        }

        public static bool ShouldTick(this Pawn pawn)
        {
            if (_finilizePhase && pawn == Current)
                return !(_finilizePhase = false);

            int tick = GenTicks.TicksGame;
            if ((pawn.thingIDNumber + tick) % 30 == 0 ||
                (tick % 250 == 0) ||
                (tick % 103 == 0) ||
                (pawn.jobs?.curJob?.expiryInterval > 0 && (tick - pawn.jobs.curJob.startTick) % (pawn.jobs.curJob.expiryInterval) == 0))
                return true;

            if (Context.DilationFastMovingRace[pawn.def.index])
                return (pawn.thingIDNumber + tick) % 2 == 0;

            return !pawn.OffScreen() ? ((pawn.thingIDNumber + tick) % DilationRateOnScreen(pawn.Position, pawn.Map) == 0) : ((pawn.thingIDNumber + tick) % DilationRateOffScreen == 0);
        }

        private static Pawn _pawnScreen;
        private static bool _offScreen;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool OffScreen(this Pawn pawn)
        {
            if (_pawnScreen == pawn)
                return _offScreen;

            if (pawn == null || pawn != Current)
                return false;

            _pawnScreen = pawn;
            if (RocketDebugPrefs.AlwaysDilating)
                _offScreen = true;
            else
            {
                _offScreen = true;

                foreach (var camera in MultiplayerCameraLister.PlayerCameras)
                {
                    if (camera.CameraRect.Contains(pawn.Position))
                    {
                        _offScreen = false;
                        break;
                    }
                }
            }

            return _offScreen;
        }

        private static bool _isBeingThrottled;
        private static Pawn _throttledPawn;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBeingThrottled(this Pawn pawn)
        {
            if (!Context.PartiallyDilatedContext && !WorldPawnsTicker.isActive)
            {
                if (_throttledPawn == pawn)
                    return _isBeingThrottled;

                if (pawn == null || pawn != Current)
                    return false;

                _throttledPawn = pawn;
                _isBeingThrottled = IsValidThrottleablePawn(pawn);

                return _isBeingThrottled;
            }

            return false;
        }

        public static bool IsValidThrottleablePawn(this Pawn pawn)
        {
            if (WorldPawnsTicker.isActive)
                return false;
            //if (WorldPawnsTicker.isActive)
            // return RocketPrefs.TimeDilationWorldPawns && !pawn.IsCaravanMember() && pawn.Faction != Faction.OfPlayer && pawn.HostFaction != Faction.OfPlayer && !HasHediffPreventingThrottling(pawn);
            if (MultiplayerCameraLister.PlayerCameras.Any(c => c.CurrentMap == pawn.Map && c.CameraZoom is CameraZoomRange.Closest or CameraZoomRange.Close) && !pawn.OffScreen())
                return false;

            if (!(!RocketPrefs.TimeDilationCriticalHediffs && HasHediffPreventingThrottling(pawn)) && !IgnoreMeDatabase.ShouldIgnore(pawn.def))
            {
                if (pawn.def.race.Humanlike)
                    return false;
                // if (pawn.def.race.Humanlike)
                // return (RocketPrefs.TimeDilationColonists || RocketPrefs.TimeDilationVisitors) && IsValidHuman(pawn);
                if (pawn.def.race.Animal)
                    return Context.DilationEnabled[pawn.def.index] && IsValidAnimal(pawn);
            }

            return false;
        }

        private static bool IsValidHuman(Pawn pawn)
        {
            if (Context.CurJobSettings.throttleFilter == JobThrottleFilter.Animals)
                return false;
            if (GenTicks.TicksGame - (pawn.jobs?.curJob?.startTick ?? 0) <= 30)
                return false;
            Faction playerFaction = Faction.OfPlayer;

            if (!RocketPrefs.TimeDilationColonists && pawn.factionInt == playerFaction)
                return false;
            if (!RocketPrefs.TimeDilationVisitors && pawn.factionInt != playerFaction)
                return false;

            return !IsCastingVerb(pawn);
        }

        private static bool IsValidAnimal(Pawn pawn)
        {
            if (Context.CurJobSettings.throttleFilter == JobThrottleFilter.Humanlikes)
                return false;
            RaceSettings raceSettings = Context.CurRaceSettings;

            if (pawn.factionInt == Faction.OfPlayer)
                return !raceSettings.ignorePlayerFaction && RocketPrefs.TimeDilationColonyAnimals && !IsCastingVerb(pawn);
            if (pawn.factionInt != null)
                return !raceSettings.ignoreFactions && RocketPrefs.TimeDilationVisitors && !IsCastingVerb(pawn);

            return RocketPrefs.TimeDilationWildlife && !IsCastingVerb(pawn);
        }
    }
}