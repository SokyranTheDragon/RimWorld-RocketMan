using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RocketMan;

public static class UnnaturalCorpseMultiplayerCameraPatch
{
    public static void CleanupPatches()
    {
        var methods = new[]
        {
            AccessTools.DeclaredMethod(typeof(AnomalyUtility), nameof(AnomalyUtility.IsValidUnseenCell)),
            AccessTools.DeclaredMethod(typeof(UnnaturalCorpse), nameof(UnnaturalCorpse.IsOutsideView)),
        };

        foreach (var method in methods)
        {
            var transpilers = Harmony.GetPatchInfo(method)?.Transpilers;
            if (transpilers == null)
                continue;
            if (transpilers.Any(p => p.owner == Finder.Harmony.Id))
                Finder.Harmony.Unpatch(method, HarmonyPatchType.Transpiler, "multiplayer");
        }
    }

    private static Map CurrentMapReplacement(Map targetMap)
    {
        // Out of MP, normal behaviour
        if (Multiplayer.Client.Multiplayer.Client == null)
            return Find.CurrentMap;
        // If in MP, the other patch will handle the map check as well.
        // Right now assume the map matches and move along.
        return targetMap;
    }

    private static bool DoesAnyCameraContain(ref CellRect view, IntVec3 cell, Map targetMap)
    {
        // Out of MP, normal behaviour
        if (Multiplayer.Client.Multiplayer.Client == null)
            return view.Contains(cell);
        // Camera patches failed/disabled, assume not seen
        if (!IncreaseMaxAllowedPacketId.MultiplayerCameraPatched)
            return false;

        // Check if any player with the same map sees the target
        foreach (var camera in MultiplayerCameraLister.PlayerCameras)
        {
            if (camera.CurrentMap == targetMap && camera.CameraRect.ExpandedBy(1).Contains(cell))
                return true;
        }

        return false;
    }

    private static IEnumerable<CodeInstruction> InsertPatches(IEnumerable<CodeInstruction> instr, Func<IEnumerable<CodeInstruction>> insertTargetMap)
    {
        var currentMapCall = AccessTools.DeclaredPropertyGetter(typeof(Find), nameof(Find.CurrentMap));
        var currentMapReplacement = AccessTools.DeclaredMethod(typeof(UnnaturalCorpseMultiplayerCameraPatch), nameof(CurrentMapReplacement));

        var containsCall = AccessTools.DeclaredMethod(typeof(CellRect), nameof(CellRect.Contains), [typeof(IntVec3)]);
        var containsReplacement = AccessTools.DeclaredMethod(typeof(UnnaturalCorpseMultiplayerCameraPatch), nameof(DoesAnyCameraContain));

        foreach (var ci in instr)
        {
            yield return ci;

            MethodInfo targetMethod = null;
            if (ci.Calls(currentMapCall))
                targetMethod = currentMapReplacement;
            else if (ci.Calls(containsCall))
                targetMethod = containsReplacement;

            if (targetMethod != null)
            {
                var replaced = false;
                foreach (var targetMapInstr in insertTargetMap().Concat(new CodeInstruction(OpCodes.Call, targetMethod)))
                {
                    // Replace the current instr opcode/operand rather than returning a new
                    // instruction to preserve any labels and branching instructions.
                    if (!replaced)
                    {
                        ci.opcode = targetMapInstr.opcode;
                        ci.operand = targetMapInstr.operand;

                        replaced = true;
                    }
                    else yield return targetMapInstr;
                }
            }
        }
    }

    [RocketPatch(typeof(AnomalyUtility), nameof(AnomalyUtility.IsValidUnseenCell))]
    private static class PatchAnomalyUtility
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            return InsertPatches(instr, TargetMapCall);

            IEnumerable<CodeInstruction> TargetMapCall()
            {
                return [new CodeInstruction(OpCodes.Ldarg_2)];
            }
        }
    }

    [RocketPatch(typeof(UnnaturalCorpse), nameof(UnnaturalCorpse.IsOutsideView))]
    private static class PatchUnnaturalCorpse
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var mapHeldCall = AccessTools.DeclaredPropertyGetter(typeof(Thing), nameof(Thing.MapHeld));

            return InsertPatches(instr, TargetMapCall);

            IEnumerable<CodeInstruction> TargetMapCall()
            {
                return
                [
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, mapHeldCall),
                ];
            }
        }
    }
}