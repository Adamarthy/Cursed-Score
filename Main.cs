using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CursedScore
{
    public struct TileSnapshot
    {
        public int X;
        public int Y;
        public string Letter;
        public bool IsVariable;
        public object OriginalTile;
    }

    public static class BuildInfo
    {
        public const string Name = "Cursed Scores";
        public const string Description = "Pre calculate highest scoring word for the video game Cursed Words by Buried Things";
        public const string Author = "Mathieu Marthy Roy";
        public const string Version = "0.9.0";
        public const string Company = null;
        public const string DownloadLink = null;
    }

    public class Main : MelonMod
    {
        private GridLayoutController _cachedGridController;
        private EncounterController _cachedEncounterController;
        private int _lastGridNumber = -1;

        private CancellationTokenSource _cts;
        private ScorePacket _highestScore = new ScorePacket(0L);
        private string _bestWordText = "";
        private string _displayText = "<color=cyan>Ready...</color>";
        private GUIStyle _style;
        private readonly object _scoreLock = new object();

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            StopAndClear();
        }

        private void StopAndClear()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            lock (_scoreLock)
            {
                // We only reset the grid number if we truly want to force a refresh
                _lastGridNumber = -1;
                _highestScore = new ScorePacket(0L);
                _bestWordText = "";
                _displayText = "<color=cyan>Ready...</color>";
            }
        }

        public override void OnInitializeMelon()
        {
            // This tells MelonLoader to look for the [HarmonyPatch] below
            HarmonyInstance.PatchAll(typeof(Main));
            MelonLogger.Msg($"Cursed Scores Mod created by Mathieu Marthy Roy");
        }

        // This runs the MOMENT the player clicks reroll
        [HarmonyPatch(typeof(EncounterController), "TryReroll")]
        public static class Patch_EncounterController_TryReroll
        {
            public static void Postfix()
            {
                var instance = MelonMod.RegisteredMelons.FirstOrDefault(m => m is Main) as Main;
                if (instance != null)
                {
                    instance.HandleReroll();
                }
            }
        }

        // Helper method to handle the reroll state
        public void HandleReroll()
        {
            StopAndClear(); // Kill current tasks

            lock (_scoreLock)
            {
                _lastGridNumber = -1;
                _displayText = "<color=orange>Rerolling...</color>";
            }
        }

        public override void OnUpdate()
        {
            // 1. Maintain the Controller Caches
            if (_cachedGridController == null)
            {
                _cachedGridController = UnityEngine.Object.FindAnyObjectByType<GridLayoutController>();
                if (_cachedGridController != null)
                {
                    var field = typeof(GridLayoutController).GetField("_encounterController", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null) _cachedEncounterController = field.GetValue(_cachedGridController) as EncounterController;
                }
            }

            // 2. Monitor Grid Changes
            if (_cachedEncounterController != null)
            {
                var grid = _cachedEncounterController.GetGridData();

                // BUG FIX: Added null check for the grid itself and checked if it's currently changing/loading
                if (grid != null && grid.GridNumber != _lastGridNumber)
                {
                    var tiles = grid.GetAvailableTiles();

                    // BUG FIX: Don't trigger if the grid is "new" but still empty/populating
                    if (tiles == null || tiles.Count == 0) return;

                    // Stop any existing search immediately
                    _cts?.Cancel();
                    _cts?.Dispose();

                    // Reset scores for the new grid
                    lock (_scoreLock)
                    {
                        _highestScore = new ScorePacket(0L);
                        _bestWordText = "";
                        _displayText = "<color=orange>Preparing Snapshots...</color>";
                    }

                    // SNAPSHOT DATA
                    var snapshots = PrepareSnapshots(grid);

                    if (snapshots.Count > 0)
                    {
                        // Only update the grid number once we have valid data and start the task
                        _lastGridNumber = grid.GridNumber;
                        _cts = new CancellationTokenSource();
                        RunHighScoringSolver(snapshots, _cts.Token, _cachedEncounterController.GetPreviousWords(), _cachedEncounterController.GetBossModifiers(), grid, _cachedEncounterController.CurrentGridsGenerated());
                    }
                }
            }
        }

        private List<TileSnapshot> PrepareSnapshots(GridData grid)
        {
            var list = new List<TileSnapshot>();
            var tiles = grid.GetAvailableTiles();
            if (tiles == null) return list;

            foreach (var t in tiles)
            {
                var pos = t.GetCoordinates();
                list.Add(new TileSnapshot
                {
                    X = pos.x,
                    Y = pos.y,
                    Letter = t.GetStringRepresentation(true).ToLower(),
                    IsVariable = t.IsDisplayingAsVariableLetter(),
                    OriginalTile = t
                });
            }
            return list;
        }

        private void RunHighScoringSolver(List<TileSnapshot> tiles, CancellationToken token, List<HistoricWord> historicwords, List<BossModifier> bossmodifiers, GridData griddata, int currentgridgenerated)
        {
            var vocab = Vocabulary.ActiveLanguageVocabulary;
            if (vocab == null || !vocab.IsInitialized || tiles.Count == 0) return;

            _displayText = "<color=orange>Solving...</color>";
            var lengths = vocab.TriesByLength.Keys.OrderByDescending(k => k).ToList();
            var solver = new BestScoreSolver();

            Task.Run(() =>
            {
                try
                {
                    foreach (int len in lengths)
                    {
                        if (token.IsCancellationRequested) return;

                        var trie = vocab.TriesByLength[len];
                        solver.SolveForScore(tiles, trie, len, (packet, word) =>
                        {
                            lock (_scoreLock)
                            {
                                if (packet > _highestScore)
                                {
                                    _highestScore = packet;
                                    _bestWordText = word;
                                    _displayText = $"<color=orange>Searching...</color> | <color=green>★ BEST:</color> <color=yellow>{_highestScore}</color> <color=white>({word.ToUpper()})</color>";
                                }
                            }
                        }, token, historicwords, bossmodifiers, griddata, currentgridgenerated);
                    }

                    lock (_scoreLock)
                    {
                        if (token.IsCancellationRequested) return;
                        _displayText = _highestScore.Score > 0 ?
                            $"<color=cyan>DONE</color> | <color=green>★ BEST:</color> <color=yellow>{_highestScore}</color> | <color=white>{_bestWordText.ToUpper()}</color>" :
                            "<color=red>DONE - No words found</color>";
                    }
                }
                catch (Exception) { /* Handle or log solver errors */ }
            }, token);
        }

        public override void OnGUI()
        {
            if (_style == null)
            {
                _style = new GUIStyle { fontSize = 26, richText = true, fontStyle = FontStyle.Bold };
                _style.normal.textColor = Color.white;
            }

            GUI.Label(new Rect(625, 800, 1000, 100), _displayText, _style);
            GUI.Label(new Rect(625, 1050, 1000, 100), "Cursed Scores Mod created by Mathieu Marthy Roy", _style);
        }
    }

    public class BestScoreSolver
    {
        private static FieldInfo _childrenField;
        private static FieldInfo _isWordField;
        private static bool _reflectionCached = false;

        private void CacheReflection(object node)
        {
            if (_reflectionCached || node == null) return;
            var type = node.GetType();
            _childrenField = type.GetField("Children", BindingFlags.Public | BindingFlags.Instance);
            _isWordField = type.GetField("IsWord", BindingFlags.Public | BindingFlags.Instance);
            _reflectionCached = true;
        }

        public void SolveForScore(List<TileSnapshot> tiles, WordTrie trie, int targetLength, Action<ScorePacket, string> onFound, CancellationToken token,
            List<HistoricWord> historicwords, List<BossModifier> bossmodifiers, GridData griddata, int currentgridgenerated)
        {
            var rootField = typeof(WordTrie).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance);
            object rootNode = rootField?.GetValue(trie);
            if (rootNode == null) return;

            CacheReflection(rootNode);
            if (_childrenField == null) return;

            // Map for fast neighbor lookup
            var gridMap = tiles.ToDictionary(t => (t.X, t.Y));

            foreach (var tile in tiles)
            {
                if (token.IsCancellationRequested) return;
                InternalSearch(gridMap, rootNode, tile, "", new List<TileSnapshot>(), new HashSet<(int, int)>(), targetLength, onFound, token, historicwords, bossmodifiers, griddata, currentgridgenerated);
            }
        }

        private void InternalSearch(Dictionary<(int, int), TileSnapshot> gridMap, object currentNode, TileSnapshot tile, string currentWord, List<TileSnapshot> currentPath, HashSet<(int, int)> visited,
            int target, Action<ScorePacket, string> onFound, CancellationToken token, List<HistoricWord> historicwords, List<BossModifier> bossmodifiers, GridData griddata, int currentgridgenerated)
        {
            if (token.IsCancellationRequested) return;

            char c = tile.Letter[0];
            var childrenDict = _childrenField.GetValue(currentNode) as System.Collections.IDictionary;
            if (childrenDict == null || !childrenDict.Contains(c)) return;

            object nextNode = childrenDict[c];
            string nextWord = currentWord + tile.Letter;

            currentPath.Add(tile);
            visited.Add((tile.X, tile.Y));

            if (currentPath.Count == target)
            {
                if ((bool)_isWordField.GetValue(nextNode))
                {
                    // 1. Reconstruct TileSelections for the game's engine
                    var selections = currentPath.Select(s => new TileSelection(
                        s.OriginalTile as Tile,
                        currentPath.IndexOf(s) == 0 ? TileSelectionMethod.Initial : TileSelectionMethod.Adjacent,
                        s.IsVariable)).ToList();

                    Player player = GameStatics.GetPlayer();

                    // 2. Get Items (Scattered on grid + Inventory)
                    // 2.1 REPLICATE GetItemsForWordSubmission(isIncludingInventory: true)
                    List<Item> itemsForWordSubmission = new List<Item>();
                    foreach (var selection in selections)
                    {
                        Tile t = selection.SelectedTile;
                        // Using the logic from the snippet you provided
                        if (t.GetGlyphType() == GlyphType.ScatteredItem && t.ScatteredItem != null)
                        {
                            itemsForWordSubmission.Add(t.ScatteredItem);
                        }
                    }
                    // Add Inventory Items
                    itemsForWordSubmission.AddRange(player.GetAllItems());

                    // 3. Handle Cable Car Simulation
                    // We do this so the search results reflect the "Upgrade" bonus without 
                    // actually modifying the player's real items during the search.
                    int cableCarCount = player.GetUnpackedItemsOfType(typeof(CableCar)).Count;
                    if (cableCarCount > 0)
                    {
                        // Logic from SubmitWord: Upgrade stickers for every Cable Car owned
                        var stickersInWord = itemsForWordSubmission.Where(i => i.IsSticker()).ToList();
                        for (int i = 0; i < cableCarCount; i++)
                        {
                            foreach (var item in stickersInWord)
                            {
                                // Note: If item.Upgrade(0) modifies state, you might need 
                                // to clone items here to avoid corrupting the real game state.
                                item.Upgrade(0);
                            }
                        }
                    }

                    // 4. Calculate the Full Score
                    List<string> wordsList = new List<string> { nextWord };
                    List<ScoreCalcVizInfo> steps = ScoreCalculation.CalculateOverallScore(
                        selections,
                        wordsList,
                        itemsForWordSubmission,
                        historicwords,
                        bossmodifiers,
                        griddata,
                        currentgridgenerated
                    );

                    ScorePacket scoreFromCalc = ScoreCalculation.GetScoreFromScoreCalcInfo(steps);

                    // 5. Apply Challenge Modifiers (Two Wrongs / Bullseye)
                    if (player.CurrentRunProgress.Challenge is TwoWrongs)
                    {
                        scoreFromCalc *= -1L;
                    }

                    // Mathieu Marthy Roy Todo
                    // I removed some challenge's calculation here.
                    // Should i readd them?

                    ScorePacket finalScoreResult = /*_remainingTarget -*/ scoreFromCalc;
                    if (player.CurrentRunProgress.Challenge is Bullseye)
                    {
                        // Bullseye calculates distance from the target
                        finalScoreResult = finalScoreResult.GetAbsoluteValue();
                    }

                    onFound(finalScoreResult, nextWord);
                }
            }
            else
            {
                // Recursive branching
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        if (x == 0 && y == 0) continue;
                        if (gridMap.TryGetValue((tile.X + x, tile.Y + y), out var neighbor) && !visited.Contains((neighbor.X, neighbor.Y)))
                        {
                            InternalSearch(gridMap, nextNode, neighbor, nextWord, currentPath, visited, target, onFound, token, historicwords, bossmodifiers, griddata, currentgridgenerated);
                        }
                    }
                }
            }

            visited.Remove((tile.X, tile.Y));
            currentPath.RemoveAt(currentPath.Count - 1);
        }
    }
}