using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.Common;
using Verse;

namespace RocketMan;

[RocketPatch(typeof(ConnectionBase), "HandleReceiveMsg", parameters = new []{ typeof(int), typeof(int), typeof(ByteReader), typeof(bool) })]
public static class IncreaseMaxAllowedPacketId
{
    public static bool MultiplayerCameraPatched
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => increasedPacketSizeLimit && insertedPacketHandlers && RocketPrefs.EnableMultiplayerCameraPatches;
    }

    public static bool increasedPacketSizeLimit = false;
    public static bool insertedPacketHandlers = false;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
    {
        var finished = false;

        foreach (var ci in instr)
        {
            yield return ci;

            if (!finished && ci.opcode == OpCodes.Ldc_I4_S && ci.operand is sbyte val)
            {
                finished = true;
                ci.operand = val + 1;
            }
        }

        increasedPacketSizeLimit = finished;
        if (!finished)
            Log.Warning($"ROCKETMAN: Failed patching max multiplayer packet ID, camera-related features will be disabled (time dilation, corpse cleanup). Other features will still work fine.");
    }
}