using HarmonyLib;
using MelonLoader;
using System;
using System.Collections;
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
        public const string Version = "1.0.0";
        public const string Company = null;
        public const string DownloadLink = "https://github.com/Adamarthy/Cursed-Score";
    }

    public class Main : MelonMod
    {
        private GridLayoutController _cachedGridController;
        private EncounterController _cachedEncounterController;
        private int _lastGridNumber = -1;

        private CancellationTokenSource _cts;
        private ScorePacket _scoreTarget = new ScorePacket(0L);
        private string _bestWordText = "";
        private string _displayText = "<color=cyan>Ready...</color>";
        private GUIStyle _style;
        private GUIStyle _buttonStyle;
        private readonly object _scoreLock = new object();

        private List<Tile> _bestPathTiles = new List<Tile>();

        private enum DesiredWordScoreTarget
        {
            HighestScore,
            LowestScore,
            LongestWord,
            ShortestWord
        }
        private DesiredWordScoreTarget _currentMode = DesiredWordScoreTarget.HighestScore;

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
                _scoreTarget = new ScorePacket(0L);
                _bestWordText = "";
                _bestPathTiles.Clear();
                _displayText = "<color=cyan>Ready...</color>";
            }
        }

        public override void OnInitializeMelon()
        {
            // This tells MelonLoader to look for the [HarmonyPatch] below
            HarmonyInstance.PatchAll(typeof(Main));
            MelonLogger.Msg($"Cursed Scores Mod created by Mathieu Marthy Roy");
            MelonLogger.Msg($"https://github.com/Adamarthy/Cursed-Score");
        }

        // This runs the moment the player clicks reroll
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
            if (_cachedEncounterController != null && _cachedEncounterController.IsWaitingForWordSubmission())
            {
                var grid = _cachedEncounterController.GetGridData();
                if (grid != null && grid.GridNumber != _lastGridNumber)
                {
                    TriggerRecalculate(grid, _currentMode);
                }
            }
        }

        // Logic extracted so the Button can call it too
        private void TriggerRecalculate(GridData grid, DesiredWordScoreTarget currentTarget)
        {
            var tiles = grid.GetAvailableTiles();
            if (tiles == null || tiles.Count == 0) return;

            _cts?.Cancel();
            _cts?.Dispose();

            lock (_scoreLock)
            {
                // Initialize score based on target (Lowest/Shortest need high initial values)
                _scoreTarget = (currentTarget == DesiredWordScoreTarget.LowestScore)
                    ? new ScorePacket(long.MaxValue)
                    : new ScorePacket(0L);

                _bestWordText = "";
                _displayText = "<color=orange>Preparing Snapshots...</color>";
            }

            var snapshots = PrepareSnapshots(grid);
            if (snapshots.Count > 0)
            {
                _lastGridNumber = grid.GridNumber;
                _cts = new CancellationTokenSource();
                RunSolver(snapshots, _cts.Token, _cachedEncounterController.GetPreviousWords(), _cachedEncounterController.GetBossModifiers(), grid, _cachedEncounterController.CurrentGridsGenerated(), currentTarget);
            }
        }

        private IEnumerator ClickBestPathCoroutine(TileSelectionManager selector, List<Tile> path)
        {
            // 1. Reset the grid
            selector.ResetGrid();
            yield return new WaitForSeconds(0.15f);

            // 3. Loop through tiles and click with delay
            foreach (var tile in path)
            {
                selector.ETileClick(tile.Coordinates);
                yield return new WaitForSeconds(0.08f);
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
                list.Add(new TileSnapshot { X = pos.x, Y = pos.y, Letter = t.GetStringRepresentation(true).ToLower(), IsVariable = t.IsDisplayingAsVariableLetter(), OriginalTile = t });
            }
            return list;
        }

        private void RunSolver(List<TileSnapshot> tiles, CancellationToken token, List<HistoricWord> historicwords, List<BossModifier> bossmodifiers, GridData griddata, int currentgridgenerated, DesiredWordScoreTarget currentTarget)
        {
            var vocab = Vocabulary.ActiveLanguageVocabulary;
            if (vocab == null || !vocab.IsInitialized || tiles.Count == 0) return;

            _displayText = "<color=orange>Solving...</color>";

            // Order lengths based on what we want to find first (Optimization)
            var lengths = (currentTarget == DesiredWordScoreTarget.ShortestWord)
                ? vocab.TriesByLength.Keys.OrderBy(k => k).ToList()
                : vocab.TriesByLength.Keys.OrderByDescending(k => k).ToList();

            var solver = new BestScoreSolver();

            Task.Run(() =>
            {
                try
                {
                    foreach (int len in lengths)
                    {
                        // If a boss modifier limits length, don't even bother searching longer lengths
                        if (!Vocabulary.IsWordLengthValid(len, bossmodifiers))
                        {
                            continue;
                        }

                        if (token.IsCancellationRequested) return;

                        var trie = vocab.TriesByLength[len];
                        solver.SolveForScore(tiles, trie, len, (packet, word, path) =>
                        {
                            // MelonLogger.Msg($"Solve for {word}");
                            lock (_scoreLock)
                            {
                                bool isBetter = false;
                                switch (currentTarget)
                                {
                                    case DesiredWordScoreTarget.HighestScore:
                                        if (packet > _scoreTarget) isBetter = true;
                                        break;
                                    case DesiredWordScoreTarget.LowestScore:
                                        if (packet < _scoreTarget) isBetter = true;
                                        break;
                                    case DesiredWordScoreTarget.LongestWord:
                                        if (word.Length > _bestWordText.Length || (word.Length == _bestWordText.Length && packet > _scoreTarget)) isBetter = true;
                                        break;
                                    case DesiredWordScoreTarget.ShortestWord:
                                        if (string.IsNullOrEmpty(_bestWordText) || word.Length < _bestWordText.Length || (word.Length == _bestWordText.Length && packet > _scoreTarget)) isBetter = true;
                                        break;
                                }

                                if (isBetter)
                                {
                                    _scoreTarget = packet;
                                    _bestWordText = word;
                                    _bestPathTiles = path.Select(s => s.OriginalTile as Tile).ToList();
                                    _displayText = $"<color=orange>Searching...</color> | <color=green>★ FOUND:</color> <color=yellow>{_scoreTarget}</color> <color=white>({word.ToUpper()})</color>";
                                }
                            }
                        }, token, historicwords, bossmodifiers, griddata, currentgridgenerated);
                    }

                    lock (_scoreLock)
                    {
                        if (token.IsCancellationRequested) return;

                        _displayText = _bestWordText.Length > 0 ?
                            $"<color=cyan>DONE</color> | <color=green>★ {GetFriendlyName(currentTarget)}:</color> <color=yellow>{_scoreTarget}</color> | <color=white>{_bestWordText.ToUpper()}</color>" :
                            "<color=red>DONE - No words found</color>";

                        if (_bestPathTiles.Count > 0)
                        {
                            var field = typeof(EncounterController).GetField("_tileSelectionManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field != null)
                            {
                                TileSelectionManager selector = field.GetValue(_cachedEncounterController) as TileSelectionManager;
                                if (selector != null)
                                {
                                    MelonCoroutines.Start(ClickBestPathCoroutine(selector, _bestPathTiles));
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }
            }, token);
        }

        public override void OnGUI()
        {
            if (_cachedEncounterController == null || !_cachedEncounterController.IsWaitingForWordSubmission()) return;

            if (_style == null)
            {
                _style = new GUIStyle { fontSize = 26, richText = true, fontStyle = FontStyle.Bold };
                _style.normal.textColor = Color.white;

                _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
            }

            // 1. Buttons
            var targetValues = Enum.GetValues(typeof(DesiredWordScoreTarget));
            for (int i = 0; i < targetValues.Length; i++)
            {
                DesiredWordScoreTarget target = (DesiredWordScoreTarget)targetValues.GetValue(i);

                Rect buttonRect = new Rect(450, 600 + (i * 45), 150, 40);

                string label = GetFriendlyName(target);

                if (GUI.Button(buttonRect, label, _buttonStyle))
                {
                    if (_cachedEncounterController != null)
                    {
                        _currentMode = target;
                        TriggerRecalculate(_cachedEncounterController.GetGridData(), target);
                    }
                }
            }

            // 3. Draw original display text
            GUI.Label(new Rect(625, 795, 1000, 100), _displayText, _style);
        }

        private string GetFriendlyName(DesiredWordScoreTarget target)
        {
            switch (target)
            {
                case DesiredWordScoreTarget.HighestScore:
                    return "Highest Score";
                case DesiredWordScoreTarget.LowestScore:
                    return "Lowest Score";
                case DesiredWordScoreTarget.LongestWord:
                    return "Longest Word";
                case DesiredWordScoreTarget.ShortestWord:
                    return "Shortest Word";
                default:
                    return target.ToString();
            }
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

        public void SolveForScore(List<TileSnapshot> tiles, WordTrie trie, int targetLength, Action<ScorePacket, string, List<TileSnapshot>> onFound, CancellationToken token,
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
            int target, Action<ScorePacket, string, List<TileSnapshot>> onFound, CancellationToken token, List<HistoricWord> historicwords, List<BossModifier> bossmodifiers, GridData griddata, int currentgridgenerated)
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
                    // Should i re add them?

                    ScorePacket finalScoreResult = /*_remainingTarget -*/ scoreFromCalc;
                    if (player.CurrentRunProgress.Challenge is Bullseye)
                    {
                        // Bullseye calculates distance from the target
                        finalScoreResult = finalScoreResult.GetAbsoluteValue();
                    }

                    //WordValidity validity = Vocabulary.CheckInvalidityReason(selections.Select((TileSelection selectedTile) => selectedTile.SelectedTile).ToList(), bossmodifiers);
                    //if (validity == WordValidity.Valid)
                    {
                        onFound(finalScoreResult, nextWord, new List<TileSnapshot>(currentPath));
                    }                    
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