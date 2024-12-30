﻿using System.Collections.Generic;
using HarmonyLib;
using Heck;
using UnityEngine;

namespace NoodleExtensions.HarmonyPatches.SmallFixes;

[HeckPatch(PatchType.Features)]
[HarmonyPatch(typeof(BeatmapObjectSpawnController))]
internal class InitializedSpawnMovementData
{
    // Moves initializition of BeatmapObjectSpawnMovementData from BeatmapObjectSpawnController.Start to the Zenject intializtion phase
    private InitializedSpawnMovementData(
        BeatmapObjectSpawnController.InitData initData,
        IJumpOffsetYProvider jumpOffsetYProvider,
#if LATEST
        BeatmapObjectSpawnController spawnController)
    {
        spawnController.beatmapObjectSpawnMovementData.Init(
            initData.noteLinesCount,
            jumpOffsetYProvider,
            Vector3.right);
#else
        IBeatmapObjectSpawnController spawnController)
    {
        spawnController.beatmapObjectSpawnMovementData.Init(
            initData.noteLinesCount,
            initData.noteJumpMovementSpeed,
            initData.beatsPerMinute,
            initData.noteJumpValueType,
            initData.noteJumpValue,
            jumpOffsetYProvider,
            Vector3.right,
            Vector3.forward);
#endif
        MovementData = spawnController.beatmapObjectSpawnMovementData;
    }

    internal BeatmapObjectSpawnMovementData MovementData { get; }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(BeatmapObjectSpawnController.Start))]
    private static IEnumerable<CodeInstruction> RemoveInitTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            /*
             * -- this._beatmapObjectSpawnMovementData.Init(this._initData.noteLinesCount, this._initData.noteJumpMovementSpeed, this._initData.beatsPerMinute, this._initData.noteJumpValueType, this._initData.noteJumpValue, this._jumpOffsetYProvider, Vector3.right, Vector3.forward);
             */
            .Start()
#if LATEST
            .RemoveInstructions(9)
#else
            .RemoveInstructions(22)
#endif
            .InstructionEnumeration();
    }
}
