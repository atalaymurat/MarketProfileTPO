# MarketProfileTPO

MarketProfileTPO is a cTrader indicator that displays volume-style and TPO (Time Price Opportunity) market profiles. It can draw profiles for a selected higher timeframe while running on a chart of another timeframe.

> **Data note:** Forex tick data is not centralized traded volume. The indicator builds its volume profile from the broker's Bid/Ask quote ticks, assigning each valid tick one count at its midpoint price. Treat VPOC and LVN as quote-tick-based estimates, not exchange traded volume or bid/ask trade volume.

## What it shows

- **Volume profile:** Horizontal profile rows are built from quote ticks. Each tick is assigned to one price row; it is not spread across neighboring rows.
- **VPOC (Volume Point of Control):** The price row with the highest quote-tick count in a profile. Its row is highlighted and a horizontal ray extends to the right. An unbroken ray continues forward; after a later chart candle closes through the next price-row boundary, the ray ends and turns gray.
- **TPO profile and TPOC:** Each chart candle contributes one TPO to every price row between that candle's low and high. TPOC is the row visited by the greatest number of chart candles and is highlighted separately. TPO is calculated from chart candles, not from individual ticks.
- **TPO Value Area:** Optional VAH and VAL lines calculated around TPOC using the configured percentage of total TPO counts. The default is 68.27%.
- **LVN (Low Volume Node):** Optional dashed levels at local valleys in the tick-count distribution. LVN detection uses Gaussian smoothing and a configurable neighborhood. LVN rays end and turn gray after a later chart candle closes through the next price-row boundary.
- **Profile open/close markers:** Short horizontal markers show the selected profile period's open and close prices.
- **Diagnostics panel:** Displays the active profile, chart and tick information, row size, and calculation status.

The indicator can draw individual profile rows or only the outer profile contour. Profile row width is proportional to the row's tick count and is clipped so it does not overlap the next profile period. While a profile is forming, its width grows across elapsed chart bars so the current chart candle remains visible; closed profiles use the configured width normally.

## Add it to cTrader

1. Download or clone this repository.
2. Open `TPO_01.csproj` in an IDE with the .NET 6 SDK, or place the source in your cTrader Automate Indicators source folder.
3. Restore the NuGet packages and build the project. The project references `cTrader.Automate` and its source generators.
4. In cTrader, open **Automate**, build or refresh the indicator, then add **TPO_01** to a chart.

The chart timeframe controls the TPO block size. `Profile Time Frame` controls the boundaries at which profiles begin and end. For example, on a 1-minute chart with a 30-minute profile period, each 1-minute candle contributes TPOs to the corresponding 30-minute profile.

## Settings

Settings are grouped in cTrader's indicator parameters.

### Profile

| Setting | Default | Description |
| --- | --- | --- |
| Profile Time Frame | M30 | Higher timeframe used to start and end each profile. Options: 5M, 15M, 30M, 45M, 1H, 2H, 4H, 6H, 12H, 1D, 1W, and monthly. |
| Price Step Pips | 0 | Height of each price row in pips. At `0`, the indicator uses 20% of ATR(14) at startup as the initial row size. This startup value stays fixed while the indicator runs. Enter a positive value to set a fixed row size in pips. |
| Profiles To Show | 5 | Number of recent profiles to display, from 1 to 20. |
| Profile Color | `#FF0071C1` | Outline color for profile rows and the outer contour. |
| Volume Fill Color | `#FF0071C1` | Fill color for profile rows when row fill is enabled. |
| Volume Fill Opacity | 50% | Opacity of the volume row fill. |
| Outer Contour Only | Off | Draw only the exterior contour instead of each individual row. |
| Maximum Profile Width (Chart Bars) | 10 | Maximum profile width, measured in chart bars. |
| Profile Width Scale | 70% | Scales profile width. Use 70%, 50%, or 30%. |

### VPOC and LVN

| Setting | Default | Description |
| --- | --- | --- |
| Show VPOC | On | Show the VPOC row highlight and extension rays. |
| VPOC Color | Blue | Color for VPOC outlines and active rays. |
| Show LVN | Off | Show local low-volume valley levels and their extension rays. |
| Only Strong | On | When enabled, only show LVN candidates whose smoothed count is at most 55.3% of the profile's smoothed maximum. |
| LVN Sensitivity (Price Levels) | 5 | Number of neighboring price rows checked on each side of an LVN candidate. |
| Gaussian Smoothing Sigma (Price Levels) | 2 | Gaussian smoothing width used for LVN detection. Higher values smooth across a wider range of rows. |
| Include Profile High/Low in LVN | On | Allow qualifying LVN candidates at the profile's high or low edge, where only one side has neighbors. |
| LVN Color | Red | Color for active dashed LVN rays. Completed rays turn gray. |

### TPO

| Setting | Default | Description |
| --- | --- | --- |
| TPOC Color | Purple | Fill color used to highlight the TPOC row. |
| Show TPO Value Area | On | Show or hide the TPO Value Area High and Low lines. |
| TPO Value Area Percent | 68.27% | Target share of the profile's TPO counts included in the value area. |
| TPO Value Area Color | Turquoise | Color of the VAH and VAL lines. |

### Diagnostics

| Setting | Default | Description |
| --- | --- | --- |
| Show Diagnostics | On | Show or hide the diagnostics panel in the upper-right of the chart. |
| Diagnostic Font Size | 12 | Panel text size, from 8 to 24. |

## Calculation notes

- A profile runs from the open time of one selected profile-timeframe bar up to the next profile-timeframe bar's open time.
- For the volume profile, each valid Bid/Ask quote tick contributes one count at `(Bid + Ask) / 2`. The count goes into exactly one row, selected by the configured price step.
- For TPO, each chart candle contributes one count to each price row touched by its low-to-high range. Changing the chart timeframe therefore changes the TPO blocks and can change TPOC and the TPO value area.
- The TPO value area starts at TPOC and expands toward the adjacent row with the greater TPO count until it reaches or exceeds the configured target. When adjacent counts tie, both sides are included.
- LVN is detected from a Gaussian-smoothed version of the quote-tick row counts. Smoothing is used for LVN detection; it does not change the underlying tick counts used by the volume histogram or VPOC.
- Tick history availability depends on the broker and the data loaded by cTrader. If historical ticks are incomplete, historical profiles may only reflect the ticks available to the indicator.

## Reference methodology and sources

The TPO concepts used here follow the Market Profile framework described by **Jim Dalton**: a TPO represents a time-and-price opportunity, and the profile organizes activity across time and price. The TPOC and VPOC tie-breaking rules and the TPO Value Area expansion are implemented with **Sierra Chart's documented conventions** as a technical reference. Sierra Chart's standard TPO Value Area percentage is 70%; this indicator defaults to 68.27%, which users can change in the settings.

This is an independent cTrader implementation, not an official Jim Dalton or Sierra Chart product. Some data and mechanics differ by design: TPO blocks come from the cTrader chart candles, and the Forex volume profile counts broker quote ticks at their Bid/Ask midpoint. Therefore the VPOC/LVN distribution is not exchange-traded volume and should not be expected to match Sierra Chart's volume profile on markets with actual trade-volume data.

Sources:

- Jim Dalton Trading, [What Is the Market Profile?](https://jimdaltontrading.com/what-is-the-market-profile-2/) — Market Profile overview and the TPO as its basic time-price building block.
- Sierra Chart, [TPO Profile Charts: Technical Value Calculations](https://www.sierrachart.com/index.php?page=doc/StudiesReference/TimePriceOpportunityCharts.html) — documented TPOC/VPOC tie-breaking, TPO Value Area expansion, and default percentage.
- Sierra Chart, [Volume by Price](https://www.sierrachart.com/index.php?ID=141&Name=Volume_by_Price&page=doc/StudiesReference.php) — volume-profile concepts and calculation settings.

## License

No license file is currently included. Contact the repository owner for permission to reuse or redistribute this project.
