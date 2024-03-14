﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Chroma.EnvironmentEnhancement.Component;
using Chroma.EnvironmentEnhancement.Saved;
using Chroma.HarmonyPatches.EnvironmentComponent;
using Chroma.Settings;
using CustomJSONData.CustomBeatmap;
using Heck.Animation;
using Heck.Animation.Transform;
using SiraUtil.Affinity;
using SiraUtil.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;
using static Chroma.ChromaController;
using static Heck.HeckController;
using Object = UnityEngine.Object;

namespace Chroma.EnvironmentEnhancement
{
    // ReSharper disable UnusedMember.Global
    internal enum LookupMethod
    {
        Regex,
        Exact,
        Contains,
        StartsWith,
        EndsWith
    }

    internal class EnvironmentEnhancementManager : IAffinity
    {
        private readonly SiraLog _log;
        private readonly CustomBeatmapData _beatmapData;
        private readonly Dictionary<string, Track> _tracks;
        private readonly bool _leftHanded;
        private readonly GeometryFactory _geometryFactory;
        private readonly TrackLaneRingOffset _trackLaneRingOffset;
        private readonly ParametricBoxControllerTransformOverride _parametricBoxControllerTransformOverride;
        private readonly BeatmapObjectsAvoidanceTransformOverride _beatmapObjectsAvoidanceTransformOverride;
        private readonly DuplicateInitializer _duplicateInitializer;
        private readonly ComponentCustomizer _componentCustomizer;
        private readonly TransformControllerFactory _controllerFactory;
        private readonly Config _config;
        private readonly SavedEnvironmentLoader _savedEnvironmentLoader;
        private readonly bool _usingOverrideEnvironment;

        private EnvironmentEnhancementManager(
            SiraLog log,
            IReadonlyBeatmapData beatmapData,
            Dictionary<string, Track> tracks,
            [Inject(Id = LEFT_HANDED_ID)] bool leftHanded,
            GeometryFactory geometryFactory,
            TrackLaneRingOffset trackLaneRingOffset,
            ParametricBoxControllerTransformOverride parametricBoxControllerTransformOverride,
            BeatmapObjectsAvoidanceTransformOverride beatmapObjectsAvoidanceTransformOverride,
            DuplicateInitializer duplicateInitializer,
            ComponentCustomizer componentCustomizer,
            TransformControllerFactory controllerFactory,
            Config config,
            SavedEnvironmentLoader savedEnvironmentLoader,
            EnvironmentSceneSetupData sceneSetupData)
        {
            _beatmapData = (CustomBeatmapData)beatmapData;
            _log = log;
            _tracks = tracks;
            _leftHanded = leftHanded;
            _geometryFactory = geometryFactory;
            _trackLaneRingOffset = trackLaneRingOffset;
            _parametricBoxControllerTransformOverride = parametricBoxControllerTransformOverride;
            _beatmapObjectsAvoidanceTransformOverride = beatmapObjectsAvoidanceTransformOverride;
            _duplicateInitializer = duplicateInitializer;
            _componentCustomizer = componentCustomizer;
            _controllerFactory = controllerFactory;
            _config = config;
            _savedEnvironmentLoader = savedEnvironmentLoader;
            _usingOverrideEnvironment = sceneSetupData.hideBranding;
        }

        private static void GetChildRecursive(Transform gameObject, ref List<Transform> children)
        {
            foreach (Transform child in gameObject)
            {
                children.Add(child);
                GetChildRecursive(child, ref children);
            }
        }

        [AffinityPrefix]
        [AffinityPatch(typeof(BeatmapObjectSpawnController), nameof(BeatmapObjectSpawnController.Start))]
        private void Start(BeatmapObjectSpawnController __instance)
        {
            __instance.StartCoroutine(DelayedStart());
        }

        // TODO: add a null check on OverrideEnvironmentSettings
        private IEnumerator DelayedStart()
        {
            yield return new WaitForEndOfFrame();

            bool v2 = _beatmapData.version2_6_0AndEarlier;
            IEnumerable<CustomData>? environmentData = null;

            if (!_config.EnvironmentEnhancementsDisabled || _config.CustomEnvironmentEnabled)
            {
                // seriously what the fuck beat games
                // GradientBackground permanently yeeted because it looks awful and can ruin multi-colored chroma maps
                GameObject? gradientBackground = GameObject.Find("/Environment/GradientBackground");
                if (gradientBackground != null)
                {
                    gradientBackground.SetActive(false);
                }
            }

            if (!_config.EnvironmentEnhancementsDisabled)
            {
                environmentData = _beatmapData.customData
                    .Get<List<object>>(v2 ? V2_ENVIRONMENT : ENVIRONMENT)?
                    .Cast<CustomData>();

                if (v2)
                {
                    try
                    {
                        if (LegacyEnvironmentRemoval.Init(_beatmapData))
                        {
                            yield break;
                        }
                    }
                    catch (Exception e)
                    {
                        _log.Error("Could not run Legacy Enviroment Removal");
                        _log.Error(e);
                    }
                }
            }

            // _usingOverrideEnvironment kinda a jank way to allow forcing map environment
            if (environmentData == null && _config.CustomEnvironmentEnabled && _usingOverrideEnvironment)
            {
                // custom environment
                v2 = false;
                environmentData = _savedEnvironmentLoader.SavedEnvironment?.Environment;
            }

            if (environmentData == null)
            {
                yield break;
            }

            List<GameObjectInfo> allGameObjectInfos = GetAllGameObjects();

            if (_config.PrintEnvironmentEnhancementDebug)
            {
                _log.Debug("=====================================");
            }

            string[] gameObjectInfoIds = allGameObjectInfos.Select(n => n.FullID).ToArray();

            foreach (CustomData gameObjectData in environmentData)
            {
                int? dupeAmount = gameObjectData.Get<int?>(v2 ? V2_DUPLICATION_AMOUNT : DUPLICATION_AMOUNT);
                bool? active = gameObjectData.Get<bool?>(v2 ? V2_ACTIVE : ACTIVE);
                TransformData spawnData = new(gameObjectData, v2);
                int? lightID = gameObjectData.Get<int?>(V2_LIGHT_ID);

                List<GameObjectInfo> foundObjects;
                CustomData? geometryData = gameObjectData.Get<CustomData?>(v2 ? V2_GEOMETRY : GEOMETRY);
                if (geometryData != null)
                {
                    GameObjectInfo newObjectInfo = new(_geometryFactory.Create(geometryData, v2));
                    allGameObjectInfos.Add(newObjectInfo);
                    foundObjects = new List<GameObjectInfo> { newObjectInfo };
                    if (_config.PrintEnvironmentEnhancementDebug)
                    {
                        _log.Debug("Created new geometry object:");
                        _log.Debug(newObjectInfo.FullID);
                    }

                    // cause i know ppl are gonna fck it up
                    string? id = gameObjectData.Get<string>(GAMEOBJECT_ID);
                    LookupMethod? lookupMethod = gameObjectData.GetStringToEnum<LookupMethod?>(LOOKUP_METHOD);
                    if (id != null || lookupMethod != null)
                    {
                        throw new InvalidOperationException("you cant have geometry and an id you goofball");
                    }
                }
                else
                {
                    string id = gameObjectData.GetRequired<string>(v2 ? V2_GAMEOBJECT_ID : GAMEOBJECT_ID);
                    LookupMethod lookupMethod = gameObjectData.GetStringToEnumRequired<LookupMethod>(v2 ? V2_LOOKUP_METHOD : LOOKUP_METHOD);
                    foundObjects = LookupID.Get(allGameObjectInfos, gameObjectInfoIds, id, lookupMethod);

                    if (foundObjects.Count > 0)
                    {
                        if (_config.PrintEnvironmentEnhancementDebug)
                        {
                            _log.Debug($"ID [\"{id}\"] using method [{lookupMethod:G}] found:");
                            foundObjects.ForEach(n => _log.Debug(n.FullID));
                        }
                    }
                    else
                    {
                        _log.Error($"ID [\"{id}\"] using method [{lookupMethod:G}] found nothing");
                    }
                }

                CustomData? componentData = null;
                if (!v2)
                {
                    componentData = gameObjectData.Get<CustomData>(ComponentConstants.COMPONENTS);
                }
                else if (lightID != null)
                {
                    componentData = new CustomData(new[]
                    {
                        new KeyValuePair<string, object?>(
                            ComponentConstants.LIGHT_WITH_ID,
                            new CustomData(new[]
                            {
                                new KeyValuePair<string, object?>(LIGHT_ID, lightID.Value)
                            }))
                    });
                }

                List<GameObject> gameObjects;

                // handle duplicating
                if (dupeAmount.HasValue)
                {
                    gameObjects = new List<GameObject>();
                    if (foundObjects.Count > 100)
                    {
                        _log.Error("Extreme value reached, you are attempting to duplicate over 100 objects! Environment enhancements stopped");
                        break;
                    }

                    foreach (GameObjectInfo gameObjectInfo in foundObjects)
                    {
                        if (_config.PrintEnvironmentEnhancementDebug)
                        {
                            _log.Debug($"Duplicating [{gameObjectInfo.FullID}]:");
                        }

                        GameObject gameObject = gameObjectInfo.GameObject;
                        Transform parent = gameObject.transform.parent;
                        Scene scene = gameObject.scene;

                        for (int i = 0; i < dupeAmount.Value; i++)
                        {
                            List<IComponentData> componentDatas = new();
                            DuplicateInitializer.PrefillComponentsData(gameObject.transform, componentDatas);
                            GameObject newGameObject = Object.Instantiate(gameObject);
                            _duplicateInitializer.PostfillComponentsData(
                                newGameObject.transform,
                                gameObject.transform,
                                componentDatas);
                            SceneManager.MoveGameObjectToScene(newGameObject, scene);

                            // ReSharper disable once Unity.InstantiateWithoutParent
                            // need to move shit to right scene first
                            newGameObject.transform.SetParent(parent, true);
                            _duplicateInitializer.InitializeComponents(
                                newGameObject.transform,
                                gameObject.transform,
                                allGameObjectInfos,
                                componentDatas);

                            List<GameObjectInfo> gameObjectInfos =
                                allGameObjectInfos.Where(n => n.GameObject == newGameObject).ToList();
                            gameObjects.AddRange(gameObjectInfos.Select(n => n.GameObject));

                            if (_config.PrintEnvironmentEnhancementDebug)
                            {
                                gameObjectInfos.ForEach(n => _log.Debug(n.FullID));
                            }
                        }
                    }

                    // Update array with new duplicated objects
                    gameObjectInfoIds = allGameObjectInfos.Select(n => n.FullID).ToArray();
                }
                else
                {
                    if (lightID.HasValue)
                    {
                        _log.Error("LightID requested but no duplicated object to apply to");
                    }

                    gameObjects = foundObjects.Select(n => n.GameObject).ToList();
                }

                foreach (GameObject gameObject in gameObjects)
                {
                    if (active.HasValue)
                    {
                        gameObject.SetActive(active.Value);
                    }

                    Transform transform = gameObject.transform;

                    spawnData.Apply(transform, _leftHanded, v2);

                    // Handle TrackLaneRing
                    TrackLaneRing? trackLaneRing = gameObject.GetComponentInChildren<TrackLaneRing>();
                    if (trackLaneRing != null)
                    {
                        _trackLaneRingOffset.SetTransform(trackLaneRing, spawnData);
                    }

                    // Handle ParametricBoxController
                    ParametricBoxController parametricBoxController =
                        gameObject.GetComponentInChildren<ParametricBoxController>();
                    if (parametricBoxController != null)
                    {
                        _parametricBoxControllerTransformOverride.SetTransform(parametricBoxController, spawnData);
                    }

                    // Handle BeatmapObjectsAvoidance
                    BeatmapObjectsAvoidance beatmapObjectsAvoidance =
                        gameObject.GetComponentInChildren<BeatmapObjectsAvoidance>();
                    if (beatmapObjectsAvoidance != null)
                    {
                        _beatmapObjectsAvoidanceTransformOverride.SetTransform(beatmapObjectsAvoidance, spawnData);
                    }

                    if (componentData != null)
                    {
                        _componentCustomizer.Customize(transform, componentData);
                    }

                    List<Track>? track = gameObjectData.GetNullableTrackArray(_tracks, v2)?.ToList();
                    if (track == null)
                    {
                        continue;
                    }

                    TransformController controller = _controllerFactory.Create(gameObject, track, true);
                    if (trackLaneRing != null)
                    {
                        controller.RotationUpdated += () => _trackLaneRingOffset.UpdateRotation(trackLaneRing);
                        controller.PositionUpdated += () => TrackLaneRingOffset.UpdatePosition(trackLaneRing);
                    }
                    else if (parametricBoxController != null)
                    {
                        controller.PositionUpdated += () => _parametricBoxControllerTransformOverride.UpdatePosition(parametricBoxController);
                        controller.ScaleUpdated += () => _parametricBoxControllerTransformOverride.UpdateScale(parametricBoxController);
                    }
                    else if (beatmapObjectsAvoidance != null)
                    {
                        controller.RotationUpdated += () => _beatmapObjectsAvoidanceTransformOverride.UpdateRotation(beatmapObjectsAvoidance);
                        controller.PositionUpdated += () => _beatmapObjectsAvoidanceTransformOverride.UpdatePosition(beatmapObjectsAvoidance);
                    }

                    track.ForEach(n => n.AddGameObject(gameObject));
                }

                if (_config.PrintEnvironmentEnhancementDebug)
                {
                    _log.Debug("=====================================");
                }
            }
        }

        private List<GameObjectInfo> GetAllGameObjects()
        {
            List<GameObjectInfo> result = new();

            // I'll probably revist this formula for getting objects by only grabbing the root objects and adding all the children
            List<GameObject> gameObjects = Resources.FindObjectsOfTypeAll<GameObject>().Where(n =>
            {
                if (n == null)
                {
                    return false;
                }

                string sceneName = n.scene.name;
                if (sceneName == null)
                {
                    return false;
                }

                return (sceneName.Contains("Environment") && !sceneName.Contains("Menu")) || n.GetComponent<TrackLaneRing>() != null;
            }).ToList();

            // Adds the children of whitelist GameObjects
            // Mainly for grabbing cone objects in KaleidoscopeEnvironment
            gameObjects.ToList().ForEach(n =>
            {
                List<Transform> allChildren = new();
                GetChildRecursive(n.transform, ref allChildren);

                foreach (Transform transform in allChildren)
                {
                    if (!gameObjects.Contains(transform.gameObject))
                    {
                        gameObjects.Add(transform.gameObject);
                    }
                }
            });

            List<string> objectsToPrint = new();

            foreach (GameObject gameObject in gameObjects)
            {
                GameObjectInfo gameObjectInfo = new(gameObject);
                result.Add(new GameObjectInfo(gameObject));
                objectsToPrint.Add(gameObjectInfo.FullID);
            }

            // ReSharper disable once InvertIf
            if (_config.PrintEnvironmentEnhancementDebug)
            {
                objectsToPrint.Sort();
                objectsToPrint.ForEach(n => _log.Debug(n));
            }

            return result;
        }
    }
}
