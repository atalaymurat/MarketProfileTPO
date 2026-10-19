using System.Linq;

namespace cAlgo.Indicators;

public enum ProfilePeriodOption
{
    M5,
    M15,
    M30,
    M45,
    H1,
    H2,
    H4,
    H6,
    H12,
    D1,
    W1,
    Month1
}

public partial class TPO_01 : Indicator
{
    private readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>> _drawnObjects = new();
    private readonly System.Collections.Generic.Dictionary<int, VpocRayState> _vpocRayStates = new();
    private readonly System.Collections.Generic.Dictionary<(int Profile, int Level), VpocRayState> _lvnRayStates = new();
    private readonly System.Collections.Generic.Dictionary<int, ProfileDataCache> _profileData = new();
    private readonly System.Collections.Generic.HashSet<int> _liveProfiles = new();
    private int _lastProfileRows;
    private string _lastDrawingError = "none";
    private ChartStaticText _diagnosticsPanel;
    private AverageTrueRange _atr;
    private Bars _periodBars;
    private Ticks _ticks;
    private double _effectivePriceStep;
    private bool _usingAtrDefault;
    private long _lastProfileTickCount;
    private long _lastTicksProcessed;
    private double _lastProfileUpdateMilliseconds;
    private double _averageProfileUpdateMilliseconds;
    private string _tickHistoryStatus = "not loaded";
    private double? _lastVpocPrice;
    private long _lastVpocCount;
    private double? _lastTpocPrice;
    private long _lastTpocCount;
    private double? _lastTpoValueAreaHigh;
    private double? _lastTpoValueAreaLow;

    private sealed class VpocRayState
    {
        public double Price;
        public double CrossPrice;
        public System.DateTime StartTime;
        public System.DateTime ProfileEnd;
        public TimeSpan Interval;
        public bool IsLive;
        public bool HasCrossed;
    }

    private sealed class ProfileDataCache
    {
        public readonly System.Collections.Generic.SortedDictionary<int, double> VolumeByLevel = new();
        public readonly System.Collections.Generic.SortedDictionary<int, int> TpoByLevel = new();
        public int NextTickIndex;
        public bool TickCacheInitialized;
        public System.DateTime CachedPeriodEnd;
        public long TickCount;
        public int LastTpoBarIndex = -1;
        public System.Collections.Generic.HashSet<int> CurrentTpoLevels = new();
    }

    protected override void Initialize()
    {
        _periodBars = MarketData.GetBars(GetSelectedProfileTimeFrame());
        _ticks = MarketData.GetTicks(SymbolName);
        _ticks.Tick += OnTick;
        _atr = Indicators.AverageTrueRange(14, MovingAverageType.Exponential);
        _effectivePriceStep = Symbol.PipSize;
        _usingAtrDefault = PriceStepPips <= 0;
        if (PriceStepPips > 0)
        {
            _effectivePriceStep = PriceStepPips * Symbol.PipSize;
        }
        else if (Bars.Count > 0)
        {
            var initialAtr = _atr.Result[Bars.Count - 1];
            var atrStep = initialAtr * 0.2;
            if (!double.IsNaN(atrStep) && !double.IsInfinity(atrStep) && atrStep > 0)
                _effectivePriceStep = atrStep;
        }

        if (ShowDiagnostics)
            CreateDiagnosticsPanel();

        if (Bars.Count > 0)
        {
            var lastBar = Bars.Count - 1;
            LoadRecentTickHistory(lastBar);
            DrawRecentProfiles(lastBar);
        }
    }

    private void OnTick(TicksTickEventArgs args)
    {
        // Calculate() redraws the active profile on the chart's current bar.
    }

    public override void Calculate(int index)
    {
        if (index != Bars.Count - 1)
            return;

        var currentProfile = GetPeriodIndex(index);
        if (currentProfile < 0)
        {
            _lastDrawingError = "No matching M30 bar for this chart time";
            DrawDiagnostics(index, -1, currentProfile);
            return;
        }

        var profileCount = Math.Max(1, Math.Min(20, ProfilesToShow));
        var firstVisibleProfile = Math.Max(0, currentProfile - (profileCount - 1));
        RemoveProfilesBefore(firstVisibleProfile);

        var drawnAny = false;
        for (var profile = firstVisibleProfile; profile <= currentProfile; profile++)
        {
            if (!TryGetProfileRange(profile, index, out var firstBar, out var lastBar))
                continue;

            var isCurrent = profile == currentProfile;
            var wasLiveAndJustClosed = profile < currentProfile && _liveProfiles.Contains(profile);
            if (isCurrent || wasLiveAndJustClosed || !_drawnObjects.ContainsKey(profile))
                DrawLatestProfile(profile, firstBar, lastBar);
            drawnAny = true;
        }

        UpdateUncrossedVpocRays(currentProfile, index);
        UpdateUncrossedLvnRays(currentProfile, index);

        if (!drawnAny)
        {
            _lastDrawingError = "Could not find chart bars inside profile period";
            DrawDiagnostics(index, -1, currentProfile);
        }
    }

    private void DrawRecentProfiles(int lastBar)
    {
        var currentProfile = GetPeriodIndex(lastBar);
        if (currentProfile < 0)
        {
            _lastDrawingError = "No matching M30 bar for this chart time";
            DrawDiagnostics(lastBar, -1, currentProfile);
            return;
        }

        var profileCount = Math.Max(1, Math.Min(20, ProfilesToShow));
        var firstVisibleProfile = Math.Max(0, currentProfile - (profileCount - 1));
        RemoveProfilesBefore(firstVisibleProfile);

        for (var profile = firstVisibleProfile; profile <= currentProfile; profile++)
        {
            if (TryGetProfileRange(profile, lastBar, out var firstBar, out var profileLastBar))
                DrawLatestProfile(profile, firstBar, profileLastBar);
        }
    }

    private void LoadRecentTickHistory(int lastChartBar)
    {
        var currentProfile = GetPeriodIndex(lastChartBar);
        if (currentProfile < 0)
        {
            _tickHistoryStatus = "No matching profile period";
            return;
        }

        var profileCount = Math.Max(1, Math.Min(20, ProfilesToShow));
        var oldestProfile = Math.Max(0, currentProfile - (profileCount - 1));
        var oldestTime = _periodBars.OpenTimes[oldestProfile];

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            if (_ticks.Count > 0 && _ticks[0].Time <= oldestTime)
                break;

            if (_ticks.LoadMoreHistory() <= 0)
                break;
        }

        if (_ticks.Count == 0)
            _tickHistoryStatus = "No tick history available";
        else if (_ticks[0].Time > oldestTime)
            _tickHistoryStatus = $"Partial history; starts {_ticks[0].Time:yyyy-MM-dd HH:mm}";
        else
            _tickHistoryStatus = $"Loaded from {_ticks[0].Time:yyyy-MM-dd HH:mm}";
    }

    private int GetPeriodIndex(int chartBarIndex)
    {
        if (_periodBars == null || _periodBars.Count == 0 || chartBarIndex < 0)
            return -1;

        return _periodBars.OpenTimes.GetIndexByTime(Bars.OpenTimes[chartBarIndex]);
    }

    private bool TryGetProfileRange(int profileIndex, int lastChartBar, out int firstChartBar, out int lastProfileChartBar)
    {
        firstChartBar = -1;
        lastProfileChartBar = -1;

        if (profileIndex < 0 || profileIndex >= _periodBars.Count)
            return false;

        var periodStart = _periodBars.OpenTimes[profileIndex];
        var mappedBar = Bars.OpenTimes.GetIndexByTime(periodStart);
        firstChartBar = Math.Max(0, mappedBar);
        while (firstChartBar < Bars.Count && Bars.OpenTimes[firstChartBar] < periodStart)
            firstChartBar++;

        if (firstChartBar >= Bars.Count || firstChartBar > lastChartBar)
            return false;

        var periodEnd = profileIndex + 1 < _periodBars.Count
            ? _periodBars.OpenTimes[profileIndex + 1]
            : System.DateTime.MaxValue;

        lastProfileChartBar = firstChartBar;
        while (lastProfileChartBar + 1 <= lastChartBar && Bars.OpenTimes[lastProfileChartBar + 1] < periodEnd)
            lastProfileChartBar++;

        return true;
    }

    private void RemoveProfilesBefore(int firstVisibleProfile)
    {
        var oldProfiles = new System.Collections.Generic.List<int>();
        foreach (var profile in _drawnObjects.Keys)
        {
            if (profile < firstVisibleProfile)
                oldProfiles.Add(profile);
        }

        foreach (var profile in oldProfiles)
        {
            foreach (var name in _drawnObjects[profile])
                Chart.RemoveObject(name);
            _drawnObjects.Remove(profile);
            _vpocRayStates.Remove(profile);
            var oldLvnRays = _lvnRayStates.Keys.Where(key => key.Profile == profile).ToList();
            foreach (var key in oldLvnRays)
                _lvnRayStates.Remove(key);
            _profileData.Remove(profile);
            _liveProfiles.Remove(profile);
        }
    }

    private void DrawLatestProfile(int profileIndex, int firstBar, int lastBar)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _lastProfileRows = DrawProfile(profileIndex, firstBar, lastBar);
            _lastDrawingError = _lastProfileTickCount == 0 ? "No ticks in selected profile period" : "none";
        }
        catch (System.Exception exception)
        {
            _lastDrawingError = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            stopwatch.Stop();
            _lastProfileUpdateMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            _averageProfileUpdateMilliseconds = _averageProfileUpdateMilliseconds == 0
                ? _lastProfileUpdateMilliseconds
                : _averageProfileUpdateMilliseconds * 0.9 + _lastProfileUpdateMilliseconds * 0.1;
        }
        DrawDiagnostics(lastBar, firstBar, profileIndex);
    }

    private void DrawDiagnostics(int index, int firstBar, int profileIndex)
    {
        if (!ShowDiagnostics)
        {
            Chart.RemoveObject("TPO01_DIAGNOSTICS");
            _diagnosticsPanel = null;
            return;
        }

        if (_diagnosticsPanel == null)
            CreateDiagnosticsPanel();

        var atrValue = _atr.Result[index];
        var step = GetPriceStep(index);
        var text = $"TPO DIAGNOSTICS\n" +
                   $"Symbol: {SymbolName} | TimeFrame: {TimeFrame}\n" +
                   $"M30 profile: {(profileIndex >= 0 && profileIndex < _periodBars.Count ? _periodBars.OpenTimes[profileIndex].ToString("yyyy-MM-dd HH:mm") : "unavailable")}\n" +
                   $"Bars: {Bars.Count} | Index: {index} | Profile bars: {firstBar}..{index}\n" +
                   $"O: {Bars.OpenPrices[index]}  H: {Bars.HighPrices[index]}  L: {Bars.LowPrices[index]}  C: {Bars.ClosePrices[index]}\n" +
                   $"Ticks in profile: {_lastProfileTickCount} | Last bar tick volume: {Bars.TickVolumes[index]}\n" +
                   $"Profile period: {ProfileTimeFrame} | Initial ATR: {atrValue}\n" +
                   $"Step: {step / Symbol.PipSize} pips ({(_usingAtrDefault ? "startup ATR x0.2, fixed" : "manual, fixed")})\n" +
                   $"MaxWidthBars: {MaxWidthBars} x {ProfileWidthScalePercent}% | Profiles shown: {_drawnObjects.Count} | Current rows: {_lastProfileRows}\n" +
                   $"Tick-count VPOC: {(_lastVpocPrice.HasValue ? _lastVpocPrice.Value.ToString() : "n/a")} ({_lastVpocCount} ticks)\n" +
                   $"1-bar TPOC: {(_lastTpocPrice.HasValue ? _lastTpocPrice.Value.ToString() : "n/a")} ({_lastTpocCount} blocks)\n" +
                   $"TPO VAH/VAL: {(_lastTpoValueAreaHigh.HasValue ? _lastTpoValueAreaHigh.Value.ToString() : "n/a")} / {(_lastTpoValueAreaLow.HasValue ? _lastTpoValueAreaLow.Value.ToString() : "n/a")}\n" +
                   $"Live redraw: {_lastProfileUpdateMilliseconds:F2} ms (avg {_averageProfileUpdateMilliseconds:F2} ms) | New ticks processed: {_lastTicksProcessed}\n" +
                   $"Tick history: {_tickHistoryStatus}\n" +
                   $"Draw status: {_lastDrawingError}";

        _diagnosticsPanel.Text = text;
    }

    private void CreateDiagnosticsPanel()
    {
        _diagnosticsPanel = Chart.DrawStaticText(
            "TPO01_DIAGNOSTICS",
            "TPO DIAGNOSTICS\nWaiting for Calculate...",
            VerticalAlignment.Top,
            HorizontalAlignment.Right,
            Color.Red);
        _diagnosticsPanel.FontSize = DiagnosticFontSize;
        _diagnosticsPanel.IsBold = true;
    }

    private int DrawProfile(int profileNumber, int firstBar, int lastBar)
    {
        _drawnObjects.TryGetValue(profileNumber, out var oldNames);
        _vpocRayStates.Remove(profileNumber);
        var oldLvnRays = _lvnRayStates.Keys.Where(key => key.Profile == profileNumber).ToList();
        foreach (var key in oldLvnRays)
            _lvnRayStates.Remove(key);

        if (!_profileData.TryGetValue(profileNumber, out var profileData))
        {
            profileData = new ProfileDataCache();
            _profileData[profileNumber] = profileData;
        }

        var step = GetPriceStep(lastBar);
        var periodStart = _periodBars.OpenTimes[profileNumber];
        var periodEnd = profileNumber + 1 < _periodBars.Count
            ? _periodBars.OpenTimes[profileNumber + 1]
            : System.DateTime.MaxValue;
        var profileBoundary = GetProfileBoundary(profileNumber, periodStart);
        var tickCountBeforeUpdate = profileData.TickCount;
        UpdateProfileVolumeCache(profileData, periodStart, periodEnd, step);
        _lastTicksProcessed = profileData.TickCount - tickCountBeforeUpdate;
        UpdateProfileTpoCache(profileData, periodEnd, firstBar, lastBar, step);
        var volumes = profileData.VolumeByLevel;
        var tpoCounts = profileData.TpoByLevel;
        _lastProfileTickCount = profileData.TickCount;

        _lastTpocPrice = null;
        _lastTpocCount = 0;
        _lastTpoValueAreaHigh = null;
        _lastTpoValueAreaLow = null;
        int? tpocLevel = null;
        if (tpoCounts.Count > 0)
        {
            var profileMid = (tpoCounts.Keys.First() + tpoCounts.Keys.Last()) / 2.0;
            var bestMidDistance = double.MaxValue;
            var bestLevel = 0;
            var bestCount = -1;

            foreach (var row in tpoCounts)
            {
                var distance = Math.Abs(row.Key - profileMid);
                if (row.Value > bestCount ||
                    (row.Value == bestCount && (distance < bestMidDistance || (distance == bestMidDistance && row.Key < bestLevel))))
                {
                    bestLevel = row.Key;
                    bestCount = row.Value;
                    bestMidDistance = distance;
                }
            }

            _lastTpocPrice = bestLevel * step + step / 2.0;
            _lastTpocCount = bestCount;
            tpocLevel = bestLevel;
        }

        var valueAreaLowLevel = 0;
        var valueAreaHighLevel = 0;
        var hasValueArea = ShowTPOValueArea && tpoCounts.Count > 0 && tpocLevel.HasValue &&
                           TryCalculateTpoValueArea(tpoCounts, tpocLevel.Value, TPOValueAreaPercent,
                               out valueAreaLowLevel, out valueAreaHighLevel);
        if (hasValueArea)
        {
            _lastTpoValueAreaHigh = (valueAreaHighLevel + 1) * step;
            _lastTpoValueAreaLow = valueAreaLowLevel * step;
        }

        _lastVpocPrice = null;
        _lastVpocCount = 0;
        int? vpocLevel = null;
        if (volumes.Count > 0)
        {
            var profileMid = (volumes.Keys.First() + volumes.Keys.Last()) / 2.0;
            var bestMidDistance = double.MaxValue;
            var bestLevel = 0;
            var bestCount = -1.0;

            foreach (var row in volumes)
            {
                var distance = Math.Abs(row.Key - profileMid);
                if (row.Value > bestCount ||
                    (row.Value == bestCount && (distance < bestMidDistance || (distance == bestMidDistance && row.Key < bestLevel))))
                {
                    bestLevel = row.Key;
                    bestCount = row.Value;
                    bestMidDistance = distance;
                }
            }

            _lastVpocPrice = bestLevel * step + step / 2.0;
            _lastVpocCount = (long)bestCount;
            vpocLevel = bestLevel;
        }

        var maxVolume = 0.0;
        foreach (var volume in volumes.Values)
            maxVolume = Math.Max(maxVolume, volume);
        var lvnVolumes = ShowLVN
            ? SmoothVolumeByLevel(volumes, LvnGaussianSigma)
            : new System.Collections.Generic.SortedDictionary<int, double>();
        var maxSmoothedVolume = 0.0;
        foreach (var volume in lvnVolumes.Values)
            maxSmoothedVolume = Math.Max(maxSmoothedVolume, volume);
        var lvnLevels = ShowLVN
            ? FindLvnLevels(lvnVolumes, maxSmoothedVolume * 0.553, OnlyStrongLVN, LvnSensitivity, IncludeProfileEdgesForLVN)
            : new System.Collections.Generic.HashSet<int>();

        var interval = GetBarInterval(lastBar);
        var profileStart = periodStart;
        var names = new System.Collections.Generic.List<string>(volumes.Count + 2);
        var color = ProfileColor;

        foreach (var row in volumes)
        {
            var widthInBars = Math.Max(0.25, MaxWidthBars * ProfileWidthScalePercent / 100.0 * row.Value / maxVolume);
            // Anchor the profile at the first bar in the calculation window and
            // extend each row to the right according to its relative volume.
            var startTime = profileStart;
            var rowDuration = TimeSpan.FromTicks((long)(interval.Ticks * widthInBars));
            var maximumDuration = profileBoundary - profileStart;
            if (rowDuration > maximumDuration)
                rowDuration = maximumDuration;
            var endTime = profileStart + rowDuration;
            var lowerPrice = row.Key * step;
            var upperPrice = lowerPrice + step;
            var name = $"TPO01_{profileNumber}_{row.Key}";
            var isVpoc = ShowVPOC && vpocLevel.HasValue && row.Key == vpocLevel.Value;
            var isTpoc = tpocLevel.HasValue && row.Key == tpocLevel.Value;
            if (!OuterContourOnly)
            {
                if (isTpoc)
                {
                    var tpocRectangle = Chart.DrawRectangle(name, startTime, upperPrice, endTime, lowerPrice, TpocColor, 1);
                    tpocRectangle.IsFilled = true;
                    tpocRectangle.IsInteractive = false;
                    names.Add(name);
                }
                else
                {
                    var fillName = $"{name}_Fill";
                    var fillColor = GetVolumeFillColor();
                    var fillRectangle = Chart.DrawRectangle(fillName, startTime, upperPrice, endTime, lowerPrice, fillColor, 1);
                    fillRectangle.IsFilled = true;
                    fillRectangle.IsInteractive = false;
                    names.Add(fillName);

                    var outlineColor = isVpoc ? VpocColor : ProfileColor;
                    var outline = Chart.DrawRectangle(name, startTime, upperPrice, endTime, lowerPrice, outlineColor, isVpoc ? 3 : 1);
                    outline.IsFilled = false;
                    outline.IsInteractive = false;
                    names.Add(name);
                }
            }

            if (OuterContourOnly && isTpoc)
            {
                var tpocName = $"TPO01_TPOC_Fill_{profileNumber}_{row.Key}";
                var tpocRectangle = Chart.DrawRectangle(tpocName, startTime, upperPrice, endTime, lowerPrice, TpocColor, 1);
                tpocRectangle.IsFilled = true;
                tpocRectangle.IsInteractive = false;
                names.Add(tpocName);
            }

            if (isTpoc && isVpoc)
            {
                var vpocOutlineName = $"TPO01_VPOC_Outline_{profileNumber}_{row.Key}";
                var vpocOutline = Chart.DrawRectangle(vpocOutlineName, startTime, upperPrice, endTime, lowerPrice, VpocColor, 3);
                vpocOutline.IsFilled = false;
                vpocOutline.IsInteractive = false;
                names.Add(vpocOutlineName);
            }

            if (ShowVPOC && vpocLevel.HasValue && row.Key == vpocLevel.Value)
            {
                var rayName = $"TPO01_VPOC_Ray_{profileNumber}";
                var isLiveProfile = periodEnd == System.DateTime.MaxValue;
                var lineStart = endTime;
                if (isLiveProfile)
                {
                    // Do not place the live line's origin beyond the latest chart data.
                    var latestVisibleTime = Bars.OpenTimes[lastBar] + interval;
                    if (lineStart > latestVisibleTime)
                        lineStart = latestVisibleTime;
                }
                else if (lineStart > periodEnd)
                {
                    // A wide histogram row can extend past its profile. Start the
                    // extension at the profile boundary so future intersections remain visible.
                    lineStart = periodEnd;
                }

                var lineEnd = isLiveProfile
                    ? lineStart + interval
                    : FindVpocCrossingTime(lineStart, periodEnd, _lastVpocPrice.Value,
                        _lastVpocPrice.Value + step / 2.0, Bars.Count - 1, interval);
                var extendToInfinity = isLiveProfile || !lineEnd.HasValue;
                var ray = Chart.DrawTrendLine(rayName, lineStart, _lastVpocPrice.Value,
                    lineEnd ?? lineStart + interval, _lastVpocPrice.Value,
                    lineEnd.HasValue ? Color.Gray : VpocColor, 2, LineStyle.Solid);
                ray.ExtendToInfinity = extendToInfinity;
                ray.IsInteractive = false;
                names.Add(rayName);
                _vpocRayStates[profileNumber] = new VpocRayState
                {
                    Price = _lastVpocPrice.Value,
                    CrossPrice = _lastVpocPrice.Value + step / 2.0,
                    StartTime = lineStart,
                    ProfileEnd = periodEnd,
                    Interval = interval,
                    IsLive = isLiveProfile,
                    HasCrossed = !isLiveProfile && lineEnd.HasValue
                };
            }

        }

        var profileOpenPrice = _periodBars.OpenPrices[profileNumber];
        var profileClosePrice = _periodBars.ClosePrices[profileNumber];
        DrawProfileOpenCloseMarkers(profileNumber, profileStart,
            profileOpenPrice, profileClosePrice, interval, names);

        if (hasValueArea)
        {
            var vah = Chart.DrawTrendLine($"TPO01_TPO_VAH_{profileNumber}", profileStart,
                (valueAreaHighLevel + 1) * step, profileBoundary, (valueAreaHighLevel + 1) * step,
                TPOValueAreaColor, 2, LineStyle.Solid);
            vah.IsInteractive = false;
            names.Add($"TPO01_TPO_VAH_{profileNumber}");

            var val = Chart.DrawTrendLine($"TPO01_TPO_VAL_{profileNumber}", profileStart,
                valueAreaLowLevel * step, profileBoundary, valueAreaLowLevel * step,
                TPOValueAreaColor, 2, LineStyle.Solid);
            val.IsInteractive = false;
            names.Add($"TPO01_TPO_VAL_{profileNumber}");
        }

        foreach (var level in lvnLevels)
        {
            var lvnName = $"TPO01_StrongLVN_Ray_{profileNumber}_{level}";
            var isLiveProfile = periodEnd == System.DateTime.MaxValue;
            var widthInBars = Math.Max(0.25,
                MaxWidthBars * ProfileWidthScalePercent / 100.0 * lvnVolumes[level] / maxSmoothedVolume);
            var lvnDuration = TimeSpan.FromTicks((long)(interval.Ticks * widthInBars));
            var lvnMaximumDuration = profileBoundary - profileStart;
            if (lvnDuration > lvnMaximumDuration)
                lvnDuration = lvnMaximumDuration;
            var lineStart = profileStart + lvnDuration;
            if (isLiveProfile)
            {
                var latestVisibleTime = Bars.OpenTimes[lastBar] + interval;
                if (lineStart > latestVisibleTime)
                    lineStart = latestVisibleTime;
            }
            else if (lineStart > periodEnd)
            {
                lineStart = periodEnd;
            }

            var lvnPrice = level * step + step / 2.0;
            var crossPrice = (level + 1) * step;
            var lineEnd = isLiveProfile
                ? (System.DateTime?)null
                : FindLvnCrossingTime(lineStart, periodEnd, lvnPrice, crossPrice, Bars.Count - 1, interval);
            var visibleChartEnd = Bars.OpenTimes[Bars.Count - 1] + interval;
            var bandEnd = lineEnd ?? visibleChartEnd;
            if (bandEnd <= lineStart)
                bandEnd = lineStart + interval;
            var profileLow = volumes.Keys.First() * step;
            var profileHigh = (volumes.Keys.Last() + 1) * step;
            var bandLow = Math.Max((level - LvnSensitivity) * step, profileLow);
            var bandHigh = Math.Min((level + LvnSensitivity + 1) * step, profileHigh);
            if (bandHigh > bandLow)
            {
                var bandName = $"TPO01_LVN_Band_{profileNumber}_{level}";
                var band = Chart.DrawRectangle(bandName, lineStart, bandHigh, bandEnd, bandLow,
                    Color.FromArgb(35, Color.Gray), 1);
                band.IsFilled = true;
                band.IsInteractive = false;
                names.Add(bandName);
            }

            var ray = Chart.DrawTrendLine(lvnName, lineStart, lvnPrice,
                lineEnd ?? visibleChartEnd, lvnPrice, lineEnd.HasValue ? Color.Gray : LvnColor, 1, LineStyle.Lines);
            ray.ExtendToInfinity = isLiveProfile || !lineEnd.HasValue;
            ray.IsInteractive = false;
            names.Add(lvnName);
            _lvnRayStates[(profileNumber, level)] = new VpocRayState
            {
                Price = lvnPrice,
                CrossPrice = crossPrice,
                StartTime = lineStart,
                ProfileEnd = periodEnd,
                Interval = interval,
                IsLive = isLiveProfile,
                HasCrossed = !isLiveProfile && lineEnd.HasValue
            };
        }

        if (OuterContourOnly)
            DrawOuterProfileContour(profileNumber, volumes, step, profileStart, profileBoundary, interval, maxVolume, names);

        if (oldNames != null)
        {
            foreach (var oldName in oldNames)
            {
                if (!names.Contains(oldName))
                    Chart.RemoveObject(oldName);
            }
        }

        _drawnObjects[profileNumber] = names;
        if (periodEnd == System.DateTime.MaxValue)
            _liveProfiles.Add(profileNumber);
        else
            _liveProfiles.Remove(profileNumber);
        return volumes.Count;
    }

    private void UpdateProfileVolumeCache(ProfileDataCache profileData, System.DateTime periodStart,
        System.DateTime periodEnd, double step)
    {
        var periodJustClosed = profileData.TickCacheInitialized &&
                               profileData.CachedPeriodEnd == System.DateTime.MaxValue &&
                               periodEnd != System.DateTime.MaxValue;
        if (!profileData.TickCacheInitialized || profileData.NextTickIndex > _ticks.Count || periodJustClosed)
        {
            profileData.VolumeByLevel.Clear();
            profileData.TickCount = 0;
            profileData.NextTickIndex = FindFirstTickAtOrAfter(periodStart);
            profileData.TickCacheInitialized = true;
        }
        profileData.CachedPeriodEnd = periodEnd;

        var tickPosition = profileData.NextTickIndex;
        while (tickPosition < _ticks.Count)
        {
            var tick = _ticks[tickPosition];
            if (tick.Time >= periodEnd)
                break;

            tickPosition++;
            if (tick.Time < periodStart || tick.Bid <= 0 || tick.Ask <= 0 || tick.Ask < tick.Bid)
                continue;

            var midPrice = (tick.Bid + tick.Ask) / 2.0;
            var level = (int)Math.Floor(midPrice / step);
            profileData.VolumeByLevel.TryGetValue(level, out var currentVolume);
            profileData.VolumeByLevel[level] = currentVolume + 1;
            profileData.TickCount++;
        }

        profileData.NextTickIndex = tickPosition;
    }

    private void UpdateProfileTpoCache(ProfileDataCache profileData, System.DateTime periodEnd,
        int firstBar, int lastBar, double step)
    {
        if (lastBar < profileData.LastTpoBarIndex)
        {
            profileData.TpoByLevel.Clear();
            profileData.CurrentTpoLevels.Clear();
            profileData.LastTpoBarIndex = -1;
        }

        var firstUnprocessedBar = profileData.LastTpoBarIndex < firstBar
            ? firstBar
            : profileData.LastTpoBarIndex + 1;

        // Previously processed bars are closed and never need another scan.
        for (var bar = firstUnprocessedBar; bar < lastBar && bar < Bars.Count; bar++)
        {
            if (Bars.OpenTimes[bar] >= periodEnd)
                break;

            AddTpoBlock(profileData.TpoByLevel, Bars.LowPrices[bar], Bars.HighPrices[bar], step);
        }

        // Recompute only the active chart bar because its high/low can still expand.
        if (profileData.LastTpoBarIndex == lastBar)
            RemoveTpoBlock(profileData.TpoByLevel, profileData.CurrentTpoLevels);

        profileData.CurrentTpoLevels = new System.Collections.Generic.HashSet<int>();
        if (lastBar >= firstBar && lastBar < Bars.Count && Bars.OpenTimes[lastBar] < periodEnd)
        {
            var lowLevel = (int)Math.Floor(Bars.LowPrices[lastBar] / step);
            var highLevel = (int)Math.Floor(Bars.HighPrices[lastBar] / step);
            for (var level = lowLevel; level <= highLevel; level++)
                profileData.CurrentTpoLevels.Add(level);

            foreach (var level in profileData.CurrentTpoLevels)
            {
                profileData.TpoByLevel.TryGetValue(level, out var count);
                profileData.TpoByLevel[level] = count + 1;
            }
        }

        profileData.LastTpoBarIndex = lastBar;
    }

    private static bool TryCalculateTpoValueArea(
        System.Collections.Generic.SortedDictionary<int, int> tpoCounts,
        int pocLevel, double valueAreaPercent, out int lowLevel, out int highLevel)
    {
        lowLevel = pocLevel;
        highLevel = pocLevel;
        if (tpoCounts.Count == 0 || !tpoCounts.ContainsKey(pocLevel))
            return false;

        long totalTpos = 0;
        foreach (var count in tpoCounts.Values)
            totalTpos += count;

        if (totalTpos <= 0)
            return false;

        var targetTpos = totalTpos * Math.Max(1.0, Math.Min(100.0, valueAreaPercent)) / 100.0;
        tpoCounts.TryGetValue(pocLevel, out var pocCount);
        long includedTpos = pocCount;
        var minLevel = tpoCounts.Keys.First();
        var maxLevel = tpoCounts.Keys.Last();

        while (includedTpos < targetTpos && (lowLevel > minLevel || highLevel < maxLevel))
        {
            var hasAbove = highLevel < maxLevel;
            var hasBelow = lowLevel > minLevel;
            var aboveLevel = highLevel + 1;
            var belowLevel = lowLevel - 1;
            tpoCounts.TryGetValue(aboveLevel, out var aboveCount);
            tpoCounts.TryGetValue(belowLevel, out var belowCount);

            if (!hasBelow || (hasAbove && aboveCount > belowCount))
            {
                highLevel = aboveLevel;
                includedTpos += aboveCount;
            }
            else if (!hasAbove || belowCount > aboveCount)
            {
                lowLevel = belowLevel;
                includedTpos += belowCount;
            }
            else
            {
                // Sierra includes both neighboring rows when their TPO counts tie.
                highLevel = aboveLevel;
                lowLevel = belowLevel;
                includedTpos += aboveCount + belowCount;
            }
        }

        return true;
    }

    private static void AddTpoBlock(System.Collections.Generic.SortedDictionary<int, int> counts,
        double lowPrice, double highPrice, double step)
    {
        var lowLevel = (int)Math.Floor(lowPrice / step);
        var highLevel = (int)Math.Floor(highPrice / step);
        for (var level = lowLevel; level <= highLevel; level++)
        {
            counts.TryGetValue(level, out var count);
            counts[level] = count + 1;
        }
    }

    private static void RemoveTpoBlock(System.Collections.Generic.SortedDictionary<int, int> counts,
        System.Collections.Generic.HashSet<int> levels)
    {
        foreach (var level in levels)
        {
            if (!counts.TryGetValue(level, out var count))
                continue;

            if (count <= 1)
                counts.Remove(level);
            else
                counts[level] = count - 1;
        }
    }

    private static System.Collections.Generic.SortedDictionary<int, double> SmoothVolumeByLevel(
        System.Collections.Generic.SortedDictionary<int, double> rawVolumes, double sigma)
    {
        var smoothed = new System.Collections.Generic.SortedDictionary<int, double>();
        if (rawVolumes.Count == 0)
            return smoothed;

        sigma = Math.Max(0.1, sigma);
        var radius = Math.Max(1, (int)Math.Ceiling(3.0 * sigma));
        var minimumLevel = rawVolumes.Keys.First();
        var maximumLevel = rawVolumes.Keys.Last();
        for (var level = minimumLevel; level <= maximumLevel; level++)
        {
            var weightedVolume = 0.0;
            var totalWeight = 0.0;
            var firstNeighbor = Math.Max(minimumLevel, level - radius);
            var lastNeighbor = Math.Min(maximumLevel, level + radius);
            for (var neighbor = firstNeighbor; neighbor <= lastNeighbor; neighbor++)
            {
                var distance = neighbor - level;
                var weight = Math.Exp(-(distance * distance) / (2.0 * sigma * sigma));
                rawVolumes.TryGetValue(neighbor, out var neighborVolume);
                weightedVolume += neighborVolume * weight;
                totalWeight += weight;
            }

            smoothed[level] = totalWeight > 0 ? weightedVolume / totalWeight : 0;
        }

        return smoothed;
    }

    private static System.Collections.Generic.HashSet<int> FindLvnLevels(
        System.Collections.Generic.SortedDictionary<int, double> volumes, double maximumVolumeThreshold,
        bool onlyStrong, int sensitivity, bool includeProfileEdges)
    {
        var valleys = new System.Collections.Generic.HashSet<int>();
        sensitivity = Math.Max(1, sensitivity);
        if (volumes.Count < 3)
            return valleys;

        var minimumLevel = volumes.Keys.First();
        var maximumLevel = volumes.Keys.Last();
        foreach (var row in volumes)
        {
            var level = row.Key;
            if (onlyStrong && row.Value > maximumVolumeThreshold)
                continue;

            var isValley = true;
            var hasHigherNeighbor = false;
            var hasLowerNeighbor = false;
            for (var distance = 1; distance <= sensitivity; distance++)
            {
                var higherLevel = level + distance;
                if (higherLevel <= maximumLevel)
                {
                    if (!volumes.TryGetValue(higherLevel, out var higherPriceVolume) || higherPriceVolume <= row.Value)
                    {
                        isValley = false;
                        break;
                    }
                    hasHigherNeighbor = true;
                }
                else if (!includeProfileEdges)
                {
                    isValley = false;
                    break;
                }

                var lowerLevel = level - distance;
                if (lowerLevel >= minimumLevel)
                {
                    if (!volumes.TryGetValue(lowerLevel, out var lowerPriceVolume) || lowerPriceVolume < row.Value)
                    {
                        isValley = false;
                        break;
                    }
                    hasLowerNeighbor = true;
                }
                else if (!includeProfileEdges)
                {
                    isValley = false;
                    break;
                }
            }

            if (isValley && hasHigherNeighbor && hasLowerNeighbor ||
                isValley && includeProfileEdges && (hasHigherNeighbor || hasLowerNeighbor))
                valleys.Add(level);
        }

        return valleys;
    }

    private void UpdateUncrossedVpocRays(int currentProfile, int currentBar)
    {
        if (!ShowVPOC)
            return;

        // Bars.Count - 1 is the forming candle. A VPOC ray is invalidated only
        // after a completed candle closes into the next price bucket.
        var closedBar = currentBar - 1;

        foreach (var entry in _vpocRayStates)
        {
            var profile = entry.Key;
            var state = entry.Value;
            if (state.HasCrossed)
                continue;

            var hasClosedPastLevel = !state.IsLive && profile < currentProfile && closedBar > 0 &&
                                     Bars.OpenTimes[closedBar] >= state.ProfileEnd &&
                                     DidCloseThroughLevel(state.Price, state.CrossPrice,
                                         Bars.ClosePrices[closedBar - 1], Bars.ClosePrices[closedBar]);
            if (hasClosedPastLevel)
            {
                var lineEnd = closedBar + 1 < Bars.Count
                    ? Bars.OpenTimes[closedBar + 1]
                    : Bars.OpenTimes[closedBar] + state.Interval;
                var ray = Chart.DrawTrendLine($"TPO01_VPOC_Ray_{profile}", state.StartTime, state.Price,
                    lineEnd, state.Price, Color.Gray, 2, LineStyle.Solid);
                ray.ExtendToInfinity = false;
                ray.IsInteractive = false;
                state.HasCrossed = true;
                continue;
            }

            var visibleEnd = currentBar + 1 < Bars.Count
                ? Bars.OpenTimes[currentBar + 1]
                : Bars.OpenTimes[currentBar] + state.Interval;
            // The live profile is redrawn on every Calculate, which can cover earlier
            // rays. Redraw each un-crossed VPOC after the profiles so it stays visible
            // above their fills and retains its infinite extension.
            var extendedRay = Chart.DrawTrendLine($"TPO01_VPOC_Ray_{profile}", state.StartTime, state.Price,
                visibleEnd, state.Price, VpocColor, 2, LineStyle.Solid);
            extendedRay.ExtendToInfinity = true;
            extendedRay.IsInteractive = false;
        }
    }

    private void UpdateUncrossedLvnRays(int currentProfile, int currentBar)
    {
        if (!ShowLVN)
            return;

        var closedBar = currentBar - 1;
        if (closedBar <= 0)
            return;

        foreach (var entry in _lvnRayStates)
        {
            var key = entry.Key;
            var state = entry.Value;
            if (key.Profile >= currentProfile || state.IsLive || state.HasCrossed)
                continue;

            var hasClosedThrough = Bars.OpenTimes[closedBar] >= state.ProfileEnd &&
                                   DidCloseThroughLevel(state.Price, state.CrossPrice,
                                       Bars.ClosePrices[closedBar - 1], Bars.ClosePrices[closedBar]);
            var rayName = $"TPO01_StrongLVN_Ray_{key.Profile}_{key.Level}";
            if (hasClosedThrough)
            {
                var lineEnd = closedBar + 1 < Bars.Count
                    ? Bars.OpenTimes[closedBar + 1]
                    : Bars.OpenTimes[closedBar] + state.Interval;
                var closedRay = Chart.DrawTrendLine(rayName, state.StartTime,
                    state.Price, lineEnd, state.Price, Color.Gray, 1, LineStyle.Lines);
                closedRay.ExtendToInfinity = false;
                closedRay.IsInteractive = false;
                state.HasCrossed = true;
                continue;
            }

            var visibleEnd = currentBar + 1 < Bars.Count
                ? Bars.OpenTimes[currentBar + 1]
                : Bars.OpenTimes[currentBar] + state.Interval;
            var openRay = Chart.DrawTrendLine(rayName, state.StartTime,
                state.Price, visibleEnd, state.Price, LvnColor, 1, LineStyle.Lines);
            openRay.ExtendToInfinity = true;
            openRay.IsInteractive = false;
        }
    }

    private void DrawOuterProfileContour(int profileNumber, System.Collections.Generic.SortedDictionary<int, double> volumes,
        double step, System.DateTime profileStart, System.DateTime profileBoundary, TimeSpan interval, double maxVolume,
        System.Collections.Generic.List<string> names)
    {
        var rows = new System.Collections.Generic.List<(int Level, System.DateTime EndTime)>();
        foreach (var row in volumes)
        {
            var widthInBars = Math.Max(0.25, MaxWidthBars * ProfileWidthScalePercent / 100.0 * row.Value / maxVolume);
            var rowDuration = TimeSpan.FromTicks((long)(interval.Ticks * widthInBars));
            var maximumDuration = profileBoundary - profileStart;
            if (rowDuration > maximumDuration)
                rowDuration = maximumDuration;
            var endTime = profileStart + rowDuration;
            rows.Add((row.Key, endTime));
        }

        var runStart = 0;
        var segment = 0;
        for (var i = 1; i <= rows.Count; i++)
        {
            if (i < rows.Count && rows[i].Level == rows[i - 1].Level + 1)
                continue;

            DrawContourRun(profileNumber, rows, runStart, i - 1, step, profileStart, names, ref segment);
            runStart = i;
        }
    }

    private void DrawContourRun(int profileNumber, System.Collections.Generic.List<(int Level, System.DateTime EndTime)> rows,
        int first, int last, double step, System.DateTime profileStart,
        System.Collections.Generic.List<string> names, ref int segment)
    {
        var bottom = rows[first].Level * step;
        var top = (rows[last].Level + 1) * step;
        DrawContourSegment(profileNumber, segment++, profileStart, bottom, profileStart, top, names);
        DrawContourSegment(profileNumber, segment++, profileStart, bottom, rows[first].EndTime, bottom, names);

        for (var i = first; i <= last; i++)
        {
            var lower = rows[i].Level * step;
            var upper = lower + step;
            DrawContourSegment(profileNumber, segment++, rows[i].EndTime, lower, rows[i].EndTime, upper, names);

            if (i < last)
                DrawContourSegment(profileNumber, segment++, rows[i].EndTime, upper, rows[i + 1].EndTime, upper, names);
        }

        DrawContourSegment(profileNumber, segment++, profileStart, top, rows[last].EndTime, top, names);
    }

    private void DrawContourSegment(int profileNumber, int segment, System.DateTime time1, double price1,
        System.DateTime time2, double price2, System.Collections.Generic.List<string> names)
    {
        var name = $"TPO01_Contour_{profileNumber}_{segment}";
        var line = Chart.DrawTrendLine(name, time1, price1, time2, price2, ProfileColor, 2, LineStyle.Solid);
        line.IsInteractive = false;
        names.Add(name);
    }

    private void DrawProfileOpenCloseMarkers(int profileNumber, System.DateTime profileStart,
        double openPrice, double closePrice, TimeSpan interval,
        System.Collections.Generic.List<string> names)
    {
        var markerDuration = TimeSpan.FromTicks(Math.Max(1, interval.Ticks));
        var openName = $"TPO01_Open_{profileNumber}";
        var open = Chart.DrawTrendLine(openName, profileStart - markerDuration, openPrice,
            profileStart, openPrice, Color.Yellow, 3, LineStyle.Solid);
        open.IsInteractive = false;
        names.Add(openName);

        var closeName = $"TPO01_Close_{profileNumber}";
        var close = Chart.DrawTrendLine(closeName, profileStart, closePrice,
            profileStart + markerDuration, closePrice, Color.Yellow, 3, LineStyle.Solid);
        close.IsInteractive = false;
        names.Add(closeName);
    }

    private Color GetVolumeFillColor()
    {
        var alpha = (int)Math.Round(255.0 * Math.Max(0, Math.Min(100, VolumeFillOpacity)) / 100.0);
        return Color.FromArgb(alpha, VolumeFillColor);
    }

    private System.DateTime GetProfileBoundary(int profileNumber, System.DateTime profileStart)
    {
        if (profileNumber + 1 < _periodBars.Count)
            return _periodBars.OpenTimes[profileNumber + 1];

        return ProfileTimeFrame switch
        {
            ProfilePeriodOption.M5 => profileStart.AddMinutes(5),
            ProfilePeriodOption.M15 => profileStart.AddMinutes(15),
            ProfilePeriodOption.M30 => profileStart.AddMinutes(30),
            ProfilePeriodOption.M45 => profileStart.AddMinutes(45),
            ProfilePeriodOption.H1 => profileStart.AddHours(1),
            ProfilePeriodOption.H2 => profileStart.AddHours(2),
            ProfilePeriodOption.H4 => profileStart.AddHours(4),
            ProfilePeriodOption.H6 => profileStart.AddHours(6),
            ProfilePeriodOption.H12 => profileStart.AddHours(12),
            ProfilePeriodOption.D1 => profileStart.AddDays(1),
            ProfilePeriodOption.W1 => profileStart.AddDays(7),
            ProfilePeriodOption.Month1 => profileStart.AddMonths(1),
            _ => profileStart.AddMinutes(30)
        };
    }

    private System.DateTime? FindVpocCrossingTime(System.DateTime lineStart, System.DateTime profileEnd,
        double price, double upperBoundary, int lastBar, TimeSpan fallbackInterval)
    {
        var searchStart = profileEnd == System.DateTime.MaxValue ? lineStart : profileEnd;
        var firstBar = Bars.OpenTimes.GetIndexByTime(searchStart);
        if (firstBar < 0)
            firstBar = 0;

        while (firstBar < Bars.Count && Bars.OpenTimes[firstBar] < searchStart)
            firstBar++;

        // Exclude the currently forming bar. Only a completed close can end a ray.
        var lastClosedBar = System.Math.Min(lastBar, Bars.Count - 2);
        for (var index = firstBar; index <= lastClosedBar && index < Bars.Count; index++)
        {
            if (index > 0 && DidCloseThroughLevel(price, upperBoundary,
                    Bars.ClosePrices[index - 1], Bars.ClosePrices[index]))
            {
                // Finish at the far edge of the crossing candle so the line visibly
                // reaches the profile where the intersection occurred.
                if (index + 1 < Bars.Count)
                    return Bars.OpenTimes[index + 1];

                return Bars.OpenTimes[index] + fallbackInterval;
            }
        }

        return null;
    }

    private System.DateTime? FindLvnCrossingTime(System.DateTime lineStart, System.DateTime profileEnd,
        double price, double upperBoundary, int lastBar, TimeSpan fallbackInterval)
    {
        var searchStart = profileEnd == System.DateTime.MaxValue ? lineStart : profileEnd;
        var firstBar = Bars.OpenTimes.GetIndexByTime(searchStart);
        if (firstBar < 0)
            firstBar = 0;

        while (firstBar < Bars.Count && Bars.OpenTimes[firstBar] < searchStart)
            firstBar++;

        var lastClosedBar = System.Math.Min(lastBar, Bars.Count - 2);
        for (var index = firstBar; index <= lastClosedBar && index < Bars.Count; index++)
        {
            if (index > 0 && DidCloseThroughLevel(price, upperBoundary,
                    Bars.ClosePrices[index - 1], Bars.ClosePrices[index]))
            {
                if (index + 1 < Bars.Count)
                    return Bars.OpenTimes[index + 1];

                return Bars.OpenTimes[index] + fallbackInterval;
            }
        }

        return null;
    }

    private bool DidCloseThroughLevel(double price, double upperBoundary, double previousClose, double close)
    {
        var lowerBoundary = 2.0 * price - upperBoundary;
        var crossedUp = previousClose < upperBoundary && close >= upperBoundary;
        var crossedDown = previousClose >= lowerBoundary && close < lowerBoundary;
        return crossedUp || crossedDown;
    }

    private int FindFirstTickAtOrAfter(System.DateTime time)
    {
        var lower = 0;
        var upper = _ticks.Count;

        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (_ticks[middle].Time < time)
                lower = middle + 1;
            else
                upper = middle;
        }

        return lower;
    }

    private double GetPriceStep(int index)
    {
        return _effectivePriceStep;
    }

    private TimeFrame GetSelectedProfileTimeFrame()
    {
        return ProfileTimeFrame switch
        {
            ProfilePeriodOption.M5 => TimeFrame.Minute5,
            ProfilePeriodOption.M15 => TimeFrame.Minute15,
            ProfilePeriodOption.M30 => TimeFrame.Minute30,
            ProfilePeriodOption.M45 => TimeFrame.Minute45,
            ProfilePeriodOption.H1 => TimeFrame.Hour,
            ProfilePeriodOption.H2 => TimeFrame.Hour2,
            ProfilePeriodOption.H4 => TimeFrame.Hour4,
            ProfilePeriodOption.H6 => TimeFrame.Hour6,
            ProfilePeriodOption.H12 => TimeFrame.Hour12,
            ProfilePeriodOption.D1 => TimeFrame.Daily,
            ProfilePeriodOption.W1 => TimeFrame.Weekly,
            ProfilePeriodOption.Month1 => TimeFrame.Monthly,
            _ => TimeFrame.Minute30
        };
    }

    private TimeSpan GetBarInterval(int index)
    {
        if (index > 0)
        {
            var interval = Bars.OpenTimes[index] - Bars.OpenTimes[index - 1];
            if (interval > TimeSpan.Zero)
                return interval;
        }

        return TimeSpan.FromMinutes(1);
    }
}
