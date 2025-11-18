using System.Collections.Generic;
using System.Linq;
using System;
using TradingPlatform.BusinessLayer;
using DivergentStrV0_1.Utils;


namespace DivergentStrV0_1.Utils
{
    public static class StaticUtils
    {
        public static Period GetPeriod(HistoricalData history)
        {
            if (history.Aggregation is HistoryAggregationTime)
            {
                var agg1 = (HistoryAggregationTime)history.Aggregation;
                return agg1.Period;
            }
            //change for a new version of API, 03/11/2025
            /*else if (history.Aggregation is HistoryAggregationTickBars)
            {
                var agg1 = (HistoryAggregationTickBars)history.Aggregation;

                return new Period(BasePeriod.Tick, agg1.TicksCount);
            }*/
            else if (history.Aggregation is HistoryAggregationTick)
            {
                var agg1 = (HistoryAggregationTick)history.Aggregation;
                return new Period();// BasePeriod.Tick);//, agg1.TicksCount);
            }
            else return new Period();
        }

        public static Indicator GenerateIndicator(string indi_names, HistoricalData hd, IList<SettingItem> indi_settings = null)
        {
            if (hd == null)
                return null;

            Indicator resoult = null;
            try
            {
                var indInfo = Core.Instance.Indicators.All.First(x => x.Name == indi_names);
                Indicator indicator = Core.Instance.Indicators.CreateIndicator(indInfo);
                if (indi_settings != null)
                    indicator.Settings = indi_settings;

                resoult = indicator;
                //HACK adding Indi Here
                hd.AddIndicator(indicator);
            }
            catch (Exception ex)
            {
                AppLog.Error("StaticUtils", "IndicatorGeneration", ex.Message);
                //StrategyLogHub.Forward("StaticUtils", "Indicator Generation Failed", loggingLevel: LoggingLevel.Error);
                //StrategyLogHub.Forward("StaticUtils", $"Failed with message : {ex.Message}", loggingLevel: LoggingLevel.Error);
            }
            return resoult;
        }
    }

    public static class InMarketUtc
    {
        // ⬇️ Imposta questi 2 orari in UTC (tu li calcoli a monte)
        // Esempio EDT: DailyBreakStartUtc=21:00, DailyBreakEndUtc=22:00
        // Esempio EST: DailyBreakStartUtc=22:00, DailyBreakEndUtc=23:00
        public static TimeOnly DailyBreakStartUtc = new(21, 0); // inizio pausa giornaliera (chiusura)
        public static TimeOnly DailyBreakEndUtc = new(22, 0); // fine pausa (riapertura)

        // Opzionale: se vuoi tener traccia del weekend per chiarezza (non serve per l’in-market)
        public static TimeOnly WeekendFriCloseStartUtc = new(21, 0); // es. ven 21:00 UTC
        public static TimeOnly WeekendSunReopenUtc = new(22, 0); // es. dom 22:00 UTC

        /// <summary>
        /// Costruisce le finestre IN-MARKET standard:
        /// - Domenica→Giovedì: DailyBreakEndUtc → DailyBreakStartUtc (overnight)
        /// Nota: il venerdì sera scatta il weekend, quindi NON apriamo un nuovo overnight.
        /// </summary>
        public static List<SimpleSessionUtc> Build()
        {
            // In-market ricorrente: open dopo la pausa, close prima della pausa del giorno successivo
            var inMarketDays = new[]
            {
            DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday,
            DayOfWeek.Wednesday, DayOfWeek.Thursday
        };

            return new List<SimpleSessionUtc>
        {
            new SimpleSessionUtc(
                name: "InMarket Sun-Thu (UTC)",
                days: inMarketDays,
                openUtc:  DailyBreakEndUtc,   // es. 22:00
                closeUtc: DailyBreakStartUtc  // es. 21:00 (→ overnight perché close <= open)
            )
        };
        }

        /// <summary>
        /// Variante parametrica: utile se vuoi passare gli orari a runtime.
        /// </summary>
        public static List<SimpleSessionUtc> Build(TimeOnly breakStartUtc, TimeOnly breakEndUtc)
        {
            DailyBreakStartUtc = breakStartUtc;
            DailyBreakEndUtc = breakEndUtc;
            return Build();
        }
    }

        public static class OffMarketUtc
    {
        // Costruiamo le 3 sessioni TARGET richieste, convertendo orari EST in UTC:
        // 1) Regular (prev day)    09:30–17:00 EST
        // 2) Overnight (prev→curr) 18:00–04:00 EST (overnight)
        // 3) Morning (curr day)    04:00–09:29 EST
        // Nota: usiamo il timezone Windows "Eastern Standard Time" per gestire automaticamente DST.

        private static readonly TimeZoneInfo EasternTZ = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        private static TimeOnly EstLocalToUtcTimeOnly(int hour, int minute)
        {
            // Usiamo la data corrente (UTC) solo per ricavare la conversione stagionale (DST vs standard)
            // Il risultato è l'orario UTC corrispondente per l'odierna stagione.
            DateTime todayEst = TimeZoneInfo.ConvertTime(DateTime.UtcNow, EasternTZ);
            var estLocal = new DateTime(todayEst.Year, todayEst.Month, todayEst.Day, hour, minute, 0, DateTimeKind.Unspecified);
            var estWithZone = DateTime.SpecifyKind(estLocal, DateTimeKind.Unspecified);
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(estWithZone, EasternTZ);
            return TimeOnly.FromDateTime(utc);
        }

        public static List<SimpleSessionUtc> Build()
        {
            // Calcolo orari UTC risultanti dalla conversione EST→UTC (sensibile al DST attuale)
            var regularOpenUtc = EstLocalToUtcTimeOnly(9, 30);
            var regularCloseUtc = EstLocalToUtcTimeOnly(17, 0);

            var overnightOpenUtc = EstLocalToUtcTimeOnly(18, 0);
            var overnightCloseUtc = EstLocalToUtcTimeOnly(4, 0); // overnight → Close <= Open in UTC in molti periodi

            var morningOpenUtc = EstLocalToUtcTimeOnly(4, 0);
            var morningCloseUtc = EstLocalToUtcTimeOnly(9, 29);

            var sessions = new List<SimpleSessionUtc>
            {
                // Regular session (prev day 09:30–17:00 EST)
                new SimpleSessionUtc(
                    name: "REGULAR (UTC)",
                    days: new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                    openUtc: regularOpenUtc,
                    closeUtc: regularCloseUtc
                ),

                // Overnight session (prev 18:00 EST → curr 04:00 EST)
                // Valida da Domenica a Giovedì (apre la sera e chiude la mattina successiva)
                new SimpleSessionUtc(
                    name: "OVERNIGHT (UTC)",
                    days: new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday },
                    openUtc: overnightOpenUtc,
                    closeUtc: overnightCloseUtc
                ),

                // Morning session (curr day 04:00–09:29 EST)
                new SimpleSessionUtc(
                    name: "MORNING (UTC)",
                    days: new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                    openUtc: morningOpenUtc,
                    closeUtc: morningCloseUtc
                )
            };

            return sessions;
        }
    }
}
