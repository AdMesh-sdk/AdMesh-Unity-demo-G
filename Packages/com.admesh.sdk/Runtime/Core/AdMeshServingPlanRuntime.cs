using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AdMesh.Core
{
    internal static class AdMeshServingPlanRuntime
    {
        private sealed class PlacementBinding
        {
            public AdMeshPlacement Placement;
            public bool TestMode;
            public bool Active = true;
        }

        private static readonly Dictionary<string, PlacementBinding> Placements = new Dictionary<string, PlacementBinding>(StringComparer.Ordinal);
        private static DateTime _nextRevisionCheckUtc = DateTime.MinValue;
        private static int _planFetchInFlight;
        private static int _revisionCheckInFlight;

        internal static void RegisterPlacement(AdMeshPlacement placement, bool testMode)
        {
            if (placement == null || string.IsNullOrWhiteSpace(placement.AdUnitId))
            {
                return;
            }

            Placements[placement.AdUnitId] = new PlacementBinding
            {
                Placement = placement,
                TestMode = testMode,
                Active = true,
            };

            RequestImmediateSync();
        }

        internal static void UnregisterPlacement(string adUnitId)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                return;
            }

            Placements.Remove(adUnitId);
        }

        internal static void SetPlacementActive(string adUnitId, bool active)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                return;
            }

            if (Placements.TryGetValue(adUnitId, out var binding))
            {
                binding.Active = active;
                if (active)
                {
                    RequestImmediateSync();
                }
            }
        }

        internal static void RequestImmediateSync()
        {
            _nextRevisionCheckUtc = DateTime.MinValue;
        }

        internal static void Tick(float deltaTime)
        {
            if (!Application.isPlaying || _planFetchInFlight > 0 || _revisionCheckInFlight > 0)
            {
                return;
            }

            var activeBindings = Placements.Values
                .Where(binding => binding.Active && binding.Placement != null)
                .ToArray();

            if (activeBindings.Length == 0)
            {
                return;
            }

            if (!Application.isFocused)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (NeedsPlanRefresh(activeBindings, now))
            {
                FetchFullPlans(activeBindings);
                return;
            }

            if (_nextRevisionCheckUtc == DateTime.MinValue || now >= _nextRevisionCheckUtc)
            {
                CheckRevisions(activeBindings, now);
            }
        }

        private static bool NeedsPlanRefresh(PlacementBinding[] activeBindings, DateTime now)
        {
            foreach (var binding in activeBindings)
            {
                if (!binding.Placement.HasServingPlan || binding.Placement.PlanValidUntilUtc == DateTime.MinValue)
                {
                    return true;
                }

                if ((binding.Placement.PlanValidUntilUtc - now).TotalSeconds <= 30)
                {
                    return true;
                }
            }

            return false;
        }

        private static void FetchFullPlans(PlacementBinding[] activeBindings)
        {
            var groupedBindings = activeBindings.GroupBy(binding => binding.TestMode);
            foreach (var group in groupedBindings)
            {
                var adUnitIds = group.Select(binding => binding.Placement.AdUnitId).Distinct().ToArray();
                _planFetchInFlight++;
                AdMeshPlugin.StartRoutine(FetchPlanRoutine(adUnitIds, group.Key));
            }
        }

        private static IEnumerator FetchPlanRoutine(string[] adUnitIds, bool testMode)
        {
            ServingPlanResponse response = null;
            yield return AdMeshPlugin.FetchServingPlanCoroutine(adUnitIds, testMode, 180, result => response = result);
            _planFetchInFlight = Math.Max(0, _planFetchInFlight - 1);

            if (response?.placements == null || response.placements.Length == 0)
            {
                ScheduleNextRevisionCheck(DateTime.UtcNow, false);
                yield break;
            }

            foreach (var placementPlan in response.placements)
            {
                if (Placements.TryGetValue(placementPlan.ad_unit_id, out var binding))
                {
                    binding.Placement.ApplyServingPlan(placementPlan);
                }
            }

            ScheduleNextRevisionCheck(DateTime.UtcNow, AnyVisiblePlacement());
        }

        private static void CheckRevisions(PlacementBinding[] activeBindings, DateTime now)
        {
            var groupedBindings = activeBindings.GroupBy(binding => binding.TestMode);
            foreach (var group in groupedBindings)
            {
                var adUnitIds = group.Select(binding => binding.Placement.AdUnitId).Distinct().ToArray();
                var revisions = group
                    .Select(binding => new PlacementRevisionState
                    {
                        ad_unit_id = binding.Placement.AdUnitId,
                        serving_revision = binding.Placement.ServingRevision ?? string.Empty,
                    })
                    .ToArray();

                _revisionCheckInFlight++;
                AdMeshPlugin.StartRoutine(CheckRevisionRoutine(adUnitIds, revisions, group.Key, now));
            }
        }

        private static IEnumerator CheckRevisionRoutine(string[] adUnitIds, PlacementRevisionState[] revisions, bool testMode, DateTime now)
        {
            ServingRevisionResponse response = null;
            yield return AdMeshPlugin.CheckServingRevisionCoroutine(adUnitIds, revisions, testMode, result => response = result);
            _revisionCheckInFlight = Math.Max(0, _revisionCheckInFlight - 1);

            if (response?.placements == null)
            {
                ScheduleNextRevisionCheck(now, AnyVisiblePlacement());
                yield break;
            }

            if (response.changed)
            {
                var affectedBindings = Placements.Values
                    .Where(binding => binding.Active && binding.Placement != null)
                    .ToArray();

                FetchFullPlans(affectedBindings);
                yield break;
            }

            ScheduleNextRevisionCheck(now, AnyVisiblePlacement());
        }

        private static bool AnyVisiblePlacement()
        {
            return Placements.Values.Any(binding => binding.Active && binding.Placement != null && binding.Placement.IsLikelyActive);
        }

        private static void ScheduleNextRevisionCheck(DateTime now, bool visibleActivity)
        {
            var seconds = visibleActivity ? 35 : 75;
            _nextRevisionCheckUtc = now.AddSeconds(seconds);
        }
    }
}
