using DivergentStrV0_1.OperationSystemAdv;
using DivergentStrV0_1.OperationSystemAdv.DDDCore;
using DivergentStrV0_1.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.Strategies
{
    internal enum TradeAction
    {
        Buy,
        Sell,
        Close,
        Revert,
        Wait
    }

    internal enum TradeSignal
    {
        OpenBuy,
        OpenSell,
        CloseLong,
        CloseSell,
        Wait,
        Unknown
    }

    internal enum InternalExpositionSide
    {
        Long,
        Short,
        Both,
        Unexposed
    }

    internal class RowanStrategy : ConditionableBase<SlTpData>
    {

        private Indicator _atrIndicator;
        private Indicator _deltaBaseIndicator;
        private Indicator _slipageAtrIndicator;
        private int _maxOpen;
        private double _totalQuantity;
        private double _maxSessionLos;
        private double _startSessionLoss;        
        private bool _sessionClosed = false;
        private bool _strategyActive = true;
        private int _verbosityFreq = 0;
        private int _verbosityFreqCount = 0;
        private bool _loadAsync;
        private List<string> _logsEntry = new List<string>();
        private List<string> _logsExits = new List<string>();
        private int _entryMinConditions;
        private int _exitMinConditions;
        private readonly List<string> _entryLineNames = new List<string>();
        private readonly List<string> _exitLineNames = new List<string>();
        private double _lotQuantity;
        private InternalExpositionSide ExposeSide = InternalExpositionSide.Unexposed;
        private int ExposedCount = 0;
        private readonly int _exposureReconcileInterval;
        private int _exposureReconcileCountdown;

        // Offset entry configuration
        private readonly int _useOffsetEntry;
        private readonly int _useOffsetLimitOrders;
        private readonly double _offsetAtrMultiplier;
        private readonly Indicator _offsetAtrIndicator;

        // Pending entry state (for offset entry feature)
        private class PendingEntry
        {
            public Side Side { get; set; }
            public double TargetPrice { get; set; }
            public double OpenPrice { get; set; }
            public SlTpData MarketData { get; set; }
            public DateTime SignalTime { get; set; }
            public string LimitOrderId { get; set; }  // Track placed limit order
            public int PlacementBarIndex { get; set; }  // Track bar when limit was placed for timeout
            public bool IsReversal { get; set; }  // Not used in new reversal system
        }
        private PendingEntry _pendingEntry = null;
        
        // NEW SINGLE-ORDER REVERSAL SYSTEM
        /// <summary>
        /// Tracks a single market order that both closes old position and opens new position
        /// Much simpler than old state machine approach
        /// </summary>
        private class ReversalOrderTracker
        {
            public string OrderId { get; set; }              // Reversal order ID
            public Side TargetSide { get; set; }             // Side we're reversing TO
            public Side OriginalSide { get; set; }           // Side we're reversing FROM
            public double OrderQuantity { get; set; }        // Total order qty (|current| + 1)
            public double FlattenQuantity { get; set; }      // Qty needed to close old position
            public double CumulativeFilled { get; set; }     // Fills so far
            public SlTpData MarketData { get; set; }         // Market data for SL/TP calc
            public DateTime InitiatedTime { get; set; }      // When reversal started
            public bool OldSlTpCancelled { get; set; }       // Flag: old protective orders cancelled
            public bool NewSlTpPlaced { get; set; }          // Flag: new protective orders placed
            public List<string> OldItemIds { get; set; }     // Item IDs from old position
            
            public bool FlattenPortionFilled => CumulativeFilled >= FlattenQuantity - 0.001;
            public bool FullyFilled => Math.Abs(CumulativeFilled - OrderQuantity) < 0.001;
        }
        private ReversalOrderTracker _activeReversal = null;
        
        // Shared state
        private readonly HashSet<string> _slMissingAlerted = new HashSet<string>();
        private double _cachedPreviousLow = double.NaN;
        private double _cachedPreviousHigh = double.NaN;
        private double _cachedAtrInTicks = double.NaN;
        private bool _loggedPivotFallback = false;
        private bool _loggedAtrFallback = false;
        private static readonly TimeSpan ItemHealthLogInterval = TimeSpan.FromSeconds(5);
        private const double SlPriceSyncToleranceMultiplier = 2.1;

        public bool AllowToTrade
        {
            //TODO: [DEBUG] Audit session loss guard to avoid false positives
            get
            {
                double realizedLoss;
                double floatingLoss;
                double totalLoss = GetSessionLoss(out realizedLoss, out floatingLoss);
                var maxLoss = totalLoss < 0 && Math.Abs(totalLoss) >= Math.Abs(_maxSessionLos);
                var sessionActive = StaticSessionManager.CurrentStatus == Status.Active;

                if (maxLoss || !sessionActive)
                    return false;
                else
                    return true;
            }
        }

        public RowanStrategy()
        {
        }

        private double GetSessionLoss(out double realizedLoss, out double floatingLoss)
        {
            realizedLoss = this.Metrics.NetProfit - this._startSessionLoss;
            floatingLoss = 0.0;

            if (this._manager != null)
            {
                foreach (var item in this._manager.Items)
                {
                    var position = item?.Position;
                    if (position?.NetPnL != null)
                        floatingLoss += position.NetPnL.Value;
                }
            }

            return realizedLoss + floatingLoss;
        }

        public RowanStrategy(Indicator DeltaBaseIndicator, Indicator atrsIndicator, int max_open, double totalquantity,
            double maxSessionLosUsd, int verbosity_frequency, int slipageAtrPeriod, double lotQuantity,
            int useOffsetEntry, int useOffsetLimitOrders, double offsetAtrMultiplier, int offsetAtrPeriod,
            int exposureReconcileInterval) : base()
        {

            this._atrIndicator = atrsIndicator;
            this._deltaBaseIndicator = DeltaBaseIndicator;
            this._maxOpen = max_open;
            AppLog.System("RowanStrategy", "Constructor", 
                $"Max open positions set to {max_open}");
            this._totalQuantity = totalquantity;
            this._maxSessionLos = maxSessionLosUsd;
            this._verbosityFreq = verbosity_frequency;
            this._slipageAtrIndicator = Core.Instance.Indicators.BuiltIn.ATR(slipageAtrPeriod, MaMode.SMA);
            this._lotQuantity = lotQuantity;
            
            // Offset entry configuration
            this._useOffsetEntry = useOffsetEntry;
            this._useOffsetLimitOrders = useOffsetLimitOrders;
            this._offsetAtrMultiplier = offsetAtrMultiplier;
            this._offsetAtrIndicator = Core.Instance.Indicators.BuiltIn.ATR(offsetAtrPeriod, MaMode.SMA);

            this._exposureReconcileInterval = Math.Max(0, exposureReconcileInterval);
            this._exposureReconcileCountdown = this._exposureReconcileInterval;
            
            // Position cleanup handler - no longer handles reversal logic
            Core.Instance.PositionRemoved += (position) =>
            {
                var manager = this._manager as TpSlPositionManager;
                var slStrategy = this.Strategy as RowanSlTpStrategy;
                var affectedItems = manager?.Items
                    .OfType<TpSlItemPosition>()
                    .Where(item => string.Equals(item.PositionId, position.Id, StringComparison.Ordinal))
                    .ToList() ?? new List<TpSlItemPosition>();

                if (affectedItems.Any())
                {
                    AppLog.System("RowanStrategy", "PositionRemoved",
                        $"Broker removed position {position.Id}; clearing {affectedItems.Count} tracked item(s)");

                    foreach (var item in affectedItems)
                    {
                        item.SetPosition(null);
                        item.TryUpdateStatus(true);
                        _slMissingAlerted.Remove(item.Id);
                        slStrategy?.ClearTickResidual(item.Id);
                    }

                    manager?.PruneOrphanedItems();
                }
            };
        }

        /// <summary>
        /// Pre-trade risk validation
        /// Checks max loss and max open positions before allowing trade
        /// </summary>
        private bool VerifyCompletelyFlat(int maxRetries = 3)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                string attemptLabel = $"Attempt {attempt + 1}/{maxRetries}";

                // Check 1: Broker positions
                var brokerPositions = Core.Instance.Positions
                    .Where(p => SymbolsMatch(p.Symbol) && AccountMatches(p.Account))
                    .ToList();

                if (brokerPositions.Any())
                {
                    var positionSummary = string.Join(", ", brokerPositions.Select(p => $"{p.Id}:{p.Side}:{p.Quantity}"));
                    AppLog.System("RowanStrategy", "FlatnessCheck",
                        $"{attemptLabel}: broker positions still open ({positionSummary})");

                    if (attempt < maxRetries - 1)
                        System.Threading.Thread.Sleep(300);
                    continue;
                }

                // Check 2: Unfilled orders (including orphaned SL/TP)
                var ourOrders = Core.Instance.Orders
                    .Where(o => SymbolsMatch(o.Symbol) && AccountMatches(o.Account) &&
                               (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled))
                    .ToList();

                if (ourOrders.Any())
                {
                    var orderSummary = string.Join(", ",
                        ourOrders.Select(o => $"{o.Id}:{o.OrderType?.Name}@{o.Price:F2}/{o.TriggerPrice:F2}"));
                    AppLog.System("RowanStrategy", "FlatnessCheck",
                        $"{attemptLabel}: cancelling {ourOrders.Count} unfilled orders ({orderSummary})");

                    foreach (var order in ourOrders)
                    {
                        try
                        {
                            order.Cancel();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("RowanStrategy", "OrderCancel", $"Failed to cancel {order.Id}: {ex.Message}");
                        }
                    }

                    if (attempt < maxRetries - 1)
                        System.Threading.Thread.Sleep(300);
                    continue;
                }

                // Check 3: Manager items
                var manager = this._manager as TpSlPositionManager;
                manager?.PruneOrphanedItems();
                RefreshExposureTracking();

                int managerActiveCount = manager?.Items
                    .OfType<TpSlItemPosition>()
                    .Count(item => item.Status != PositionManagerStatus.Closed) ?? 0;

                if (this.ExposedCount == 0)
                {
                    AppLog.Trading("RowanStrategy", "FlatnessCheck",
                        $"✅ {attemptLabel}: completely flat (managerActive={managerActiveCount})");
                    this.ExposedCount = 0;
                    this.ExposeSide = InternalExpositionSide.Unexposed;
                    EnsureExposureConsistency(false);
                    return true;
                }

                AppLog.System("RowanStrategy", "FlatnessCheck",
                    $"{attemptLabel}: local exposure still {this.ExposedCount} (managerActive={managerActiveCount})");

                if (attempt < maxRetries - 1)
                    System.Threading.Thread.Sleep(300);
            }

            AppLog.Error("RowanStrategy", "FlatnessCheck",
                $"Failed to achieve flatness after {maxRetries} attempts");
            return false;
        }

        /// <summary>
        /// Get accurate current net position from broker
        /// Returns: (netQty, side, positionIds)
        /// </summary>
        private (double netQty, Side? side, List<string> positionIds) GetCurrentNetPosition()
        {
            var brokerPositions = Core.Instance.Positions
                .Where(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol))
                .GroupBy(p => p.Id)  // De-duplicate (Rithmic may return duplicates)
                .Select(g => g.First())
                .ToList();
            
            if (!brokerPositions.Any())
                return (0, null, new List<string>());
            
            // Calculate net quantity (long = positive, short = negative)
            double netQty = brokerPositions.Sum(p => p.Side == Side.Buy ? p.Quantity : -p.Quantity);
            Side? netSide = netQty > 0 ? Side.Buy : netQty < 0 ? Side.Sell : (Side?)null;
            var posIds = brokerPositions.Select(p => p.Id).ToList();
            
            AppLog.System("RowanStrategy", "NetPosition", 
                $"Broker net: {netQty:F2} contracts, Side={netSide?.ToString() ?? "Flat"}, Positions={posIds.Count}");
            
            return (Math.Abs(netQty), netSide, posIds);
        }

        private bool PreTradeRiskCheck(string context)
        {
            // Check max session loss
            double realizedLoss;
            double floatingLoss;
            double totalLoss = GetSessionLoss(out realizedLoss, out floatingLoss);
            var maxLoss = totalLoss < 0 && Math.Abs(totalLoss) >= Math.Abs(_maxSessionLos);
            
            if (maxLoss)
            {
                AppLog.Error("RowanStrategy", "RiskCheck", 
                    $"{context}: Max session loss reached (${Math.Abs(totalLoss):F2}), blocking trade");
                return false;
            }

            // CRITICAL: Refresh exposure from broker IMMEDIATELY before check
            RefreshExposureTracking();
            
            // Verify count matches broker reality
            var brokerCount = Core.Instance.Positions
                .Where(p => SymbolsMatch(p.Symbol) && AccountMatches(p.Account))
                .GroupBy(p => p.Id)
                .Count();
            
            if (this.ExposedCount != brokerCount)
            {
                AppLog.System("RowanStrategy", "RiskCheck",
                    $"Exposure mismatch: Local={this.ExposedCount}, Broker={brokerCount}, using broker count");
                this.ExposedCount = brokerCount;
            }
            
            // Check max open positions
            if (this.ExposedCount >= this._maxOpen)
            {
                AppLog.Trading("RowanStrategy", "RiskCheck", 
                    $"{context}: Max open positions reached ({this._maxOpen}), current={this.ExposedCount}, blocking trade");
                return false;
            }
            
            // SUMMARY: Emit a compact summary of the risk gate evaluation
            double realizedDraw = realizedLoss < 0 ? -realizedLoss : 0;
            double floatingDraw = floatingLoss < 0 ? -floatingLoss : 0;
            double totalDraw = totalLoss < 0 ? -totalLoss : 0;

            AppLog.System("RowanStrategy", "RiskCheckSummary",
                $"{context}: SessionActive={StaticSessionManager.CurrentStatus==Status.Active}, RealizedLoss={realizedDraw:F2}, FloatingLoss={floatingDraw:F2}, TotalLoss={totalDraw:F2}, MaxLoss={Math.Abs(_maxSessionLos):F2}, Exposed={this.ExposedCount}/{this._maxOpen}");

            return true;  // All checks passed
        }

        /// <summary>
        /// Cancel SL/TP orders from old position after reversal flatten portion fills
        /// </summary>
        private void CancelOldProtectiveOrders(List<string> oldItemIds)
        {
            if (oldItemIds == null || !oldItemIds.Any())
            {
                AppLog.System("RowanStrategy", "ReversalCleanup", "No old items to clean up");
                return;
            }
            
            var manager = this._manager as TpSlPositionManager;
            if (manager == null)
                return;
            
            AppLog.Trading("RowanStrategy", "ReversalCleanup", 
                $"Cancelling protective orders from {oldItemIds.Count} old item(s)");
            
            foreach (var itemId in oldItemIds)
            {
                var item = manager.Items
                    .OfType<TpSlItemPosition>()
                    .FirstOrDefault(i => i.Id == itemId);
                
                if (item == null)
                    continue;
                
                // Cancel SL order
                var slOrder = item.GetStopLossOrder(this.Symbol);
                if (slOrder != null && 
                    (slOrder.Status == OrderStatus.Opened || slOrder.Status == OrderStatus.PartiallyFilled))
                {
                    try
                    {
                        slOrder.Cancel();
                        AppLog.Trading("RowanStrategy", "ReversalCleanup", 
                            $"✅ Cancelled old SL order {slOrder.Id} @ {slOrder.TriggerPrice:F2}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("RowanStrategy", "ReversalCleanup", 
                            $"Failed to cancel old SL {slOrder.Id}: {ex.Message}");
                    }
                }
                
                // Cancel TP order
                var tpOrder = item.GetTakeProfitOrder(this.Symbol);
                if (tpOrder != null && 
                    (tpOrder.Status == OrderStatus.Opened || tpOrder.Status == OrderStatus.PartiallyFilled))
                {
                    try
                    {
                        tpOrder.Cancel();
                        AppLog.Trading("RowanStrategy", "ReversalCleanup", 
                            $"✅ Cancelled old TP order {tpOrder.Id} @ {tpOrder.Price:F2}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("RowanStrategy", "ReversalCleanup", 
                            $"Failed to cancel old TP {tpOrder.Id}: {ex.Message}");
                    }
                }
                
                // Unbind from position (will be cleaned up by PositionRemoved event)
                item.SetPosition(null);
            }
        }

        /// <summary>
        /// Place SL/TP for new position after reversal completes
        /// </summary>
        private void PlaceReversalProtectiveOrders(Position newPosition, SlTpData marketData)
        {
            if (newPosition == null || this.Strategy == null)
            {
                AppLog.Error("RowanStrategy", "ReversalProtective", "Cannot place protective orders: missing position or strategy");
                return;
            }
            
            try
            {
                double entryPrice = newPosition.OpenPrice;
                Side side = newPosition.Side;
                
                AppLog.Trading("RowanStrategy", "ReversalProtective", 
                    $"Calculating SL/TP for new {side} position @ {entryPrice:F2}");
                
                // Calculate SL price
                var slPrices = this.Strategy.CalculateSl(marketData, side, entryPrice);
                if (slPrices == null || !slPrices.Any())
                {
                    AppLog.Error("RowanStrategy", "ReversalProtective", 
                        "Failed to calculate SL for reversal position");
                    return;
                }
                
                // Calculate TP price
                var tpPrices = this.Strategy.CalculateTp(marketData, side, entryPrice);
                if (tpPrices == null || !tpPrices.Any())
                {
                    AppLog.Error("RowanStrategy", "ReversalProtective", 
                        "Failed to calculate TP for reversal position");
                    return;
                }
                
                double slPrice = this.Symbol.RoundPriceToTickSize(slPrices[0]);
                double tpPrice = this.Symbol.RoundPriceToTickSize(tpPrices[0]);
                
                AppLog.Trading("RowanStrategy", "ReversalProtective", 
                    $"Calculated: SL={slPrice:F2}, TP={tpPrice:F2}");
                
                // Get order types
                var stopOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)
                    .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Stop);
                var limitOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)
                    .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Limit);
                
                if (stopOrderType == null || limitOrderType == null)
                {
                    AppLog.Error("RowanStrategy", "ReversalProtective", 
                        "Order types not available for SL/TP");
                    return;
                }
                
                // Place SL order
                var slRequest = new PlaceOrderRequestParameters
                {
                    Account = this.Account,
                    Symbol = this.Symbol,
                    Side = side == Side.Buy ? Side.Sell : Side.Buy,
                    Quantity = newPosition.Quantity,
                    OrderTypeId = stopOrderType.Id,
                    TriggerPrice = slPrice,
                    TimeInForce = TimeInForce.Day,
                    PositionId = newPosition.Id,
                    AdditionalParameters = new List<SettingItem>
                    {
                        new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
                    }
                };
                
                var slResult = PlaceOrderWithRetry(slRequest, "ReversalSL");
                if (slResult.Status == TradingOperationResultStatus.Success)
                {
                    AppLog.Trading("RowanStrategy", "ReversalProtective", 
                        $"✅ SL placed @ {slPrice:F2} for new position {newPosition.Id}");
                }
                else
                {
                    AppLog.Error("RowanStrategy", "ReversalProtective", 
                        $"Failed to place SL: {slResult.Message}");
                }
                
                // Place TP order
                var tpRequest = new PlaceOrderRequestParameters
                {
                    Account = this.Account,
                    Symbol = this.Symbol,
                    Side = side == Side.Buy ? Side.Sell : Side.Buy,
                    Quantity = newPosition.Quantity,
                    OrderTypeId = limitOrderType.Id,
                    Price = tpPrice,
                    TimeInForce = TimeInForce.Day,
                    PositionId = newPosition.Id,
                    AdditionalParameters = new List<SettingItem>
                    {
                        new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
                    }
                };
                
                var tpResult = PlaceOrderWithRetry(tpRequest, "ReversalTP");
                if (tpResult.Status == TradingOperationResultStatus.Success)
                {
                    AppLog.Trading("RowanStrategy", "ReversalProtective", 
                        $"✅ TP placed @ {tpPrice:F2} for new position {newPosition.Id}");
                }
                else
                {
                    AppLog.Error("RowanStrategy", "ReversalProtective", 
                        $"Failed to place TP: {tpResult.Message}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("RowanStrategy", "ReversalProtective", 
                    $"Error placing protective orders: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes exposure tracking from actual manager state
        /// Self-correcting method that derives exposure from real positions only
        /// </summary>
        private void RefreshExposureTracking()
        {
            EnsureExposureConsistency(false);
        }

        private void PeriodicExposureReconcile()
        {
            if (_exposureReconcileInterval <= 0)
                return;

            if (_exposureReconcileCountdown > 0)
                _exposureReconcileCountdown--;

            if (_exposureReconcileCountdown <= 0)
            {
                _exposureReconcileCountdown = _exposureReconcileInterval;
                EnsureExposureConsistency(true);
            }
        }

        private void EnsureExposureConsistency(bool allowPrune)
        {
            var manager = this._manager as TpSlPositionManager;
            if (manager == null)
            {
                this.ExposeSide = InternalExpositionSide.Unexposed;
                this.ExposedCount = 0;
                return;
            }

            // STEP 1: Get broker ground truth
            var brokerPositions = Core.Instance.Positions
                .Where(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol))
                .ToList();

            // De-duplicate by Position.Id (Rithmic may return duplicates)
            var uniquePositions = brokerPositions
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .ToList();

            double minLot = this.Symbol?.MinLot ?? 1.0;
            double netQty = uniquePositions.Sum(p => p.Quantity);
            int contractCount = (int)Math.Round(Math.Abs(netQty) / Math.Max(minLot, 1e-8));
            bool brokerFlat = Math.Abs(netQty) < Math.Max(minLot, 1e-8);

            // STEP 2: Reconcile manager items with broker positions
            var livePositionIds = uniquePositions.Select(p => p.Id).ToHashSet();
            var allItems = manager.Items.OfType<TpSlItemPosition>().ToList();

            // Items with positions that no longer exist in broker
            var orphanedItems = allItems
                .Where(item => !string.IsNullOrEmpty(item.PositionId) &&
                               !livePositionIds.Contains(item.PositionId))
                .ToList();

            if (orphanedItems.Any())
            {
                AppLog.System("RowanStrategy", "ExposureReconcile",
                    $"Found {orphanedItems.Count} orphaned items (positions no longer at broker)");

                foreach (var orphan in orphanedItems)
                {
                    orphan.SetPosition(null);  // Triggers ItemClosed → Removed from Items
                }

                // Refresh list after cleanup
                allItems = manager.Items.OfType<TpSlItemPosition>().ToList();
            }

            // Items with active positions
            var activeItems = allItems
                .Where(item => !string.IsNullOrEmpty(item.PositionId) &&
                               livePositionIds.Contains(item.PositionId))
                .ToList();

            // STEP 3: Handle duplicate items pointing to same Position.Id
            var itemsByPosition = activeItems
                .GroupBy(item => item.PositionId)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in itemsByPosition)
            {
                AppLog.System("RowanStrategy", "ExposureReconcile",
                    $"Found {group.Count()} items for position {group.Key}, keeping newest");

                var itemsToKeep = group.OrderByDescending(i => i.EntryOrder?.LastUpdateTime ?? DateTime.MinValue).Take(1);
                var itemsToRemove = group.Except(itemsToKeep).ToList();

                foreach (var stale in itemsToRemove)
                {
                    stale.SetPosition(null);  // Triggers cleanup
                }
            }

            // STEP 4: Handle pruning if requested
            if (allowPrune && brokerFlat && allItems.Any())
            {
                AppLog.System("RowanStrategy", "ExposureReconcile",
                    $"Broker flat but manager has {allItems.Count} item(s) - pruning all");

                foreach (var item in allItems.ToList())
                {
                    item.SetPosition(null);
                    item.Quit();
                }

                manager.PruneOrphanedItems();
                allItems = manager.Items.OfType<TpSlItemPosition>().ToList();
                activeItems.Clear();
            }

            // STEP 5: Set accurate count (BROKER-BASED, not item-based)
            this.ExposedCount = contractCount;

            // STEP 6: Determine side exposure
            var hasLong = netQty > 0;
            var hasShort = netQty < 0;

            if (hasLong && hasShort)
                this.ExposeSide = InternalExpositionSide.Both;
            else if (hasLong)
                this.ExposeSide = InternalExpositionSide.Long;
            else if (hasShort)
                this.ExposeSide = InternalExpositionSide.Short;
            else
                this.ExposeSide = InternalExpositionSide.Unexposed;

            AppLog.System("RowanStrategy", "ExposureReconcile",
                $"Broker: {contractCount} contracts ({netQty:F2} qty), Manager: {activeItems.Count} items, Exposed: {ExposedCount}, Side: {ExposeSide}, BrokerPositions: {uniquePositions.Count}");
        }

        private bool AccountMatches(Account account)
        {
            if (account == null || this.Account == null)
                return false;

            return string.Equals(account.Id, this.Account.Id, StringComparison.OrdinalIgnoreCase);
        }

        private bool SymbolsMatch(Symbol candidate)
        {
            if (candidate == null || this.Symbol == null)
                return false;

            if (candidate == this.Symbol)
                return true;

            if (string.Equals(candidate.Id, this.Symbol.Id, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(candidate.Name, this.Symbol.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(this.Symbol.Name) && !string.IsNullOrEmpty(candidate.Name))
            {
                if (candidate.Name.StartsWith(this.Symbol.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public void ConfigureConditions(IEnumerable<string> entryLineNames, int entryMinConditions,
                                        IEnumerable<string> exitLineNames, int exitMinConditions)
        {
            _entryMinConditions = Math.Max(0, entryMinConditions);
            _exitMinConditions = Math.Max(0, exitMinConditions);

            _entryLineNames.Clear();
            if (entryLineNames != null)
                _entryLineNames.AddRange(entryLineNames.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

            _exitLineNames.Clear();
            if (exitLineNames != null)
                _exitLineNames.AddRange(exitLineNames.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        }

        private static bool TryGetLineValue(Indicator ind, string name, out double value)
        {
            try
            {
                foreach (var ls in ind.LinesSeries)
                {
                    if (string.Equals(ls.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = ls.GetValue(offset:1);
                        return true;
                    }
                }
            }
            catch { }
            value = 0.0;
            return false;
        }

        private double GetLineValueByNameAny(string name, bool isEntry, bool allowLogging)
        {
            if (TryGetLineValue(this._atrIndicator, name, out var v1)) 
            {
                string log = v1 > 0 ? "buy" : v1 == 0 ? "wait" : "sell";
                if (allowLogging && isEntry)
                    this._logsEntry.Add($"Line {name} value: {log}");
                else if (allowLogging)
                    this._logsExits.Add($"Line {name} value: {log}");
                return v1;
            }
            if (TryGetLineValue(this._deltaBaseIndicator, name, out var v2))
            {
                string log2 = v2 > 0 ? "buy" : v2 == 0 ? "wait" : "sell";
                if (allowLogging && isEntry)
                    this._logsEntry.Add($"Line {name} value: {log2}");
                else if (allowLogging)
                    this._logsExits.Add($"Line {name} value: {log2}");
                return v2;
            }
            return 0.0;
        }

        public override void Init(HistoryRequestParameters req, Account account, bool loadAsync = false, string description = "", bool allowHeavyMetrics = false)
        {
            this.ManagerChoice = ManagerType.PositionBased;
            base.Init(req, account, loadAsync, description, allowHeavyMetrics);
            StaticSessionManager.Initialize(this.HistoryProvider);
            StaticSessionManager.TradeSessionsStatusChanged += StaticSessionManager_TradeSessionsStatusChanged;
            this._loadAsync = loadAsync;

            if (StaticSessionManager.CurrentStatus == Status.Active)
            {
                this._startSessionLoss = this.Metrics.NetProfit;
                this._sessionClosed = false;
            }

            // Subscribe to TradeAdded for reversal fill monitoring
            Core.Instance.TradeAdded += OnTradeAdded_MonitorReversals;

            if (!this._loadAsync)
            {
                this.HistoryProvider.HistoricalData.AddIndicator(this._atrIndicator);
                this.HistoryProvider.HistoricalData.AddIndicator(this._deltaBaseIndicator);
                this.HistoryProvider.HistoricalData.AddIndicator(this._slipageAtrIndicator);
                
                // Add offset ATR indicator
                if (this._useOffsetEntry != 0)
                    this.HistoryProvider.HistoricalData.AddIndicator(this._offsetAtrIndicator);
            }

        }

        /// <summary>
        /// TradeAdded event handler for monitoring reversal order fills
        /// </summary>
        private void OnTradeAdded_MonitorReversals(Trade trade)
        {
            if (trade == null)
                return;
            
            // Only process our trades
            if (!AccountMatches(trade.Account) || !SymbolsMatch(trade.Symbol))
                return;
            
            // Monitor reversal fills
            MonitorReversalFills(trade);
        }
        public override void OnVolumeDataReady()
        {
            base.OnVolumeDataReady();

            //TODO: [DEBUG] Verify async volume callbacks reattach indicators safely
            if (this._loadAsync)
            {
                if (this._atrIndicator.Count == 0)
                    this.HistoryProvider.HistoricalData.AddIndicator(this._atrIndicator);
                if (this._deltaBaseIndicator.Count == 0)
                    this.HistoryProvider.HistoricalData.AddIndicator(this._deltaBaseIndicator);
                if (this._slipageAtrIndicator.Count == 0)
                    this.HistoryProvider.HistoricalData.AddIndicator(this._slipageAtrIndicator);
                    
                // Add offset ATR in async mode
                if (this._useOffsetEntry != 0 && this._offsetAtrIndicator.Count == 0)
                    this.HistoryProvider.HistoricalData.AddIndicator(this._offsetAtrIndicator);
            }

        }

        private void StaticSessionManager_TradeSessionsStatusChanged(object sender, Status e)
        {
            if (e == Status.Active)
            {
                //TODO: [DEBUG] Reset session loss baseline when status becomes Active
                this._startSessionLoss = this.Metrics.NetProfit;
                this._sessionClosed = false;
            }
        }

        protected override List<HistoryUpdAteType> GetUpdateTypes()
        {
            var types = new List<HistoryUpdAteType> { HistoryUpdAteType.NewItem };
            
            // ALWAYS subscribe to ticks for trailing SL validation
                types.Add(HistoryUpdAteType.UpdateItem);
                
            return types;
        }

        public override void Dispose()
        {
            ClearPendingEntry("Strategy disposal");
            
            // Clear active reversal if any
            _activeReversal = null;
            
            // Unsubscribe from events
            StaticSessionManager.TradeSessionsStatusChanged -= StaticSessionManager_TradeSessionsStatusChanged;
            Core.Instance.TradeAdded -= OnTradeAdded_MonitorReversals;
            
            // Dispose indicators
            this._atrIndicator.Dispose();
            this._deltaBaseIndicator.Dispose();
            this._slipageAtrIndicator.Dispose();
            
            if (this._useOffsetEntry != 0)
                this._offsetAtrIndicator?.Dispose();

            base.Dispose();
        }

        public override double SetQuantity()
        {
            if (this._lotQuantity > 0)
            {
                try
                {
                    // CRITICAL: Check if historical data is available before accessing
                    if (this.HistoryProvider?.HistoricalData == null || this.HistoryProvider.HistoricalData.Count == 0)
                    {
                        AppLog.System("RowanStrategy", "SetQuantity", "Historical data not ready yet - using lot quantity directly");
                        return this._lotQuantity > 0 ? this._lotQuantity : 1.0;
                    }
                    
                    return this._lotQuantity * this.Symbol.LotSize * this.HistoryProvider.HistoricalData[0][PriceType.Close];
                }
                catch (Exception ex)
                {
                    AppLog.Error("RowanStrategy", "SetQuantityFailed", 
                        $"Failed to compute lot-based quantity: {ex.Message}, fallback to lot quantity directly");
                    return this._lotQuantity > 0 ? this._lotQuantity : 1.0;
                }
            }
            else
                return this._totalQuantity / this._maxOpen;
        }

        protected override double CalculateContractQuantity()
        {
            if (this._lotQuantity > 0)
                return this.RoundQuantity(this._lotQuantity);

            return base.CalculateContractQuantity();
        }

        /// <summary>
        /// Monitor reversal order fills and manage SL/TP lifecycle
        /// Called from TradeAdded event
        /// </summary>
        private void MonitorReversalFills(Trade trade)
        {
            if (_activeReversal == null)
                return;
            
            // Check if this trade belongs to our reversal order
            // Note: trade.OrderId matches the reversal order we placed
            if (trade.OrderId != _activeReversal.OrderId)
                return;
            
            // Verify trade is for our symbol/account
            if (!AccountMatches(trade.Account) || !SymbolsMatch(trade.Symbol))
                return;
            
            // Accumulate filled quantity
            _activeReversal.CumulativeFilled += trade.Quantity;
            
            AppLog.Trading("RowanStrategy", "ReversalFill", 
                $"Reversal fill: {trade.Quantity} @ {trade.Price:F2}, " +
                $"cumulative {_activeReversal.CumulativeFilled}/{_activeReversal.OrderQuantity} " +
                $"(flatten needs {_activeReversal.FlattenQuantity})");
            
            // Step 1: Old position flattened? Cancel old SL/TP
            if (_activeReversal.FlattenPortionFilled && !_activeReversal.OldSlTpCancelled)
            {
                AppLog.Trading("RowanStrategy", "ReversalFlatten", 
                    $"✅ Flatten portion complete ({_activeReversal.CumulativeFilled:F2} >= {_activeReversal.FlattenQuantity:F2}), " +
                    $"cancelling old protective orders");
                
                CancelOldProtectiveOrders(_activeReversal.OldItemIds);
                _activeReversal.OldSlTpCancelled = true;
                
                // Refresh exposure tracking
                RefreshExposureTracking();
            }
            
            // Step 2: Reversal fully filled? Recalculate and ensure SL/TP with actual fill price
            if (_activeReversal.FullyFilled && !_activeReversal.NewSlTpPlaced)
            {
                AppLog.Trading("RowanStrategy", "ReversalComplete", 
                    $"✅ Reversal order FULLY FILLED ({_activeReversal.CumulativeFilled:F2}/{_activeReversal.OrderQuantity}), " +
                    $"recalculating SL/TP with actual fill price");
                
                // Small delay to let broker finalize position and manager bind it
                System.Threading.Thread.Sleep(500);
                
                // Refresh exposure tracking
                RefreshExposureTracking();
                
                // Find the new position
                var newPosition = Core.Instance.Positions
                    .Where(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol))
                    .FirstOrDefault(p => p.Side == _activeReversal.TargetSide);
                
                if (newPosition != null)
                {
                    double actualFillPrice = newPosition.OpenPrice;
                    
                    AppLog.Trading("RowanStrategy", "ReversalComplete", 
                        $"✅ New position found: {newPosition.Id}, {newPosition.Side} {newPosition.Quantity} @ {actualFillPrice:F2}");
                    
                    // Recalculate SL/TP using ACTUAL fill price (not estimated price)
                    var actualMarketData = _activeReversal.MarketData;
                    actualMarketData.currentPrice = actualFillPrice;  // Use actual fill price!
                    
                    var recalcSlPrices = this.Strategy?.CalculateSl(actualMarketData, newPosition.Side, actualFillPrice);
                    var recalcTpPrices = this.Strategy?.CalculateTp(actualMarketData, newPosition.Side, actualFillPrice);
                    
                    if (recalcSlPrices != null && recalcSlPrices.Count > 0 && 
                        recalcTpPrices != null && recalcTpPrices.Count > 0)
                    {
                        double actualSl = this.Symbol.RoundPriceToTickSize(recalcSlPrices[0]);
                        double actualTp = this.Symbol.RoundPriceToTickSize(recalcTpPrices[0]);
                        
                        // Find the manager item and update planned prices with actual fill-based calculations
                        var manager = this._manager as TpSlPositionManager;
                        var reversalItem = manager?.Items
                            .OfType<TpSlItemPosition>()
                            .FirstOrDefault(i => i.Position?.Id == newPosition.Id || 
                                                 (i.Side == newPosition.Side && i.Position == null));
                        
                        if (reversalItem != null)
                        {
                            AppLog.Trading("RowanStrategy", "ReversalComplete", 
                                $"Recalculated SL/TP: SL={actualSl:F2} (was {reversalItem.ExpectedSlPrice:F2}), " +
                                $"TP={actualTp:F2} (was {reversalItem.ExpectedTpPrice:F2})");
                            
                            // Update the item's expected prices with actual calculations
                            reversalItem.PlanStopPrice(actualSl);
                            reversalItem.PlanTakeProfitPrice(actualTp);
                            
                            // Trigger manager to place/update SL/TP with corrected prices
                            manager.EnsureChildOrders(reversalItem);
                            
                            AppLog.Trading("RowanStrategy", "ReversalComplete", 
                                $"✅ SL/TP placed via manager for position {newPosition.Id}");
                        }
                        else
                        {
                            AppLog.Error("RowanStrategy", "ReversalComplete", 
                                "Manager item not found for reversal position - SL/TP may not be placed!");
                        }
                    }
                    
                    _activeReversal.NewSlTpPlaced = true;
                }
                else
                {
                    // Position not found - check if partial fill
                    var (netQty, netSide, _) = GetCurrentNetPosition();
                    
                    if (!netSide.HasValue || netQty < this.Symbol.MinLot)
                    {
                        // Partial fill - only flatten portion executed
                        AppLog.Trading("RowanStrategy", "ReversalPartial", 
                            "⚠️ PARTIAL FILL: Position flattened but new side not opened. " +
                            "This is SAFE - will wait for new signal to enter.");
                        _activeReversal.NewSlTpPlaced = true;  // Mark as handled
                    }
                    else
                    {
                        AppLog.Error("RowanStrategy", "ReversalComplete", 
                            $"Position mismatch: expected {_activeReversal.TargetSide} {_activeReversal.OrderQuantity - _activeReversal.FlattenQuantity}, got {netSide} {netQty}");
                        _activeReversal.NewSlTpPlaced = true;  // Mark as handled to avoid infinite loop
                    }
                }
                
                // Clear tracker
                AppLog.Trading("RowanStrategy", "ReversalComplete", 
                    $"Reversal sequence complete, clearing tracker");
                _activeReversal = null;
            }
            
            // Safety check: if order is fully filled or cancelled, ensure we clear tracker
            var reversalOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == _activeReversal.OrderId);
            if (reversalOrder == null || 
                reversalOrder.Status == OrderStatus.Filled || 
                reversalOrder.Status == OrderStatus.Cancelled)
            {
                if (!_activeReversal.FullyFilled)
                {
                    AppLog.Trading("RowanStrategy", "ReversalTerminate", 
                        $"Reversal order terminated with status {reversalOrder?.Status.ToString() ?? "MISSING"}, " +
                        $"filled {_activeReversal.CumulativeFilled}/{_activeReversal.OrderQuantity}");
                }
                
                // Final cleanup
                if (_activeReversal != null && !_activeReversal.NewSlTpPlaced)
                    _activeReversal = null;
            }
        }

        /// <summary>
        /// NEW SINGLE-ORDER REVERSAL SYSTEM
        /// Execute reversal using a single market order that both closes old position 
        /// and opens new position in opposite direction
        /// This is MUCH simpler than the old state machine approach
        /// </summary>
        private bool ExecuteSingleOrderReversal(SlTpData marketData, TradeSignal entrySignal, TradeSignal exitSignal)
        {
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"═══ REVERSAL DETECTED: EntrySignal={entrySignal}, ExitSignal={exitSignal} ═══");
            
            // Log entry/exit details
            foreach (var logsitem in this._logsEntry)
                AppLog.Trading("RowanStrategy", "EntryDetails", $"Entry Details: {logsitem}");
            foreach (var logsExitem in this._logsExits)
                AppLog.Trading("RowanStrategy", "ExitDetails", $"Exit Details: {logsExitem}");
            
            // Step 1: Get current broker position
            var (netQty, currentSide, oldPositionIds) = GetCurrentNetPosition();
            
            // Safety check: if already in reversal, block duplicate
            if (_activeReversal != null)
            {
                AppLog.Trading("RowanStrategy", "Reversal", 
                    $"Reversal already in progress (order {_activeReversal.OrderId}), blocking duplicate");
                return false;
            }
            
            // Safety check: if no position, do normal entry instead
            if (!currentSide.HasValue || netQty < this.Symbol.MinLot)
            {
                AppLog.Trading("RowanStrategy", "Reversal", 
                    $"No position to reverse (netQty={netQty:F2}, side={currentSide?.ToString() ?? "null"}) - executing normal entry instead");
                
                // No position to reverse - execute normal entry
                Side targetSide = entrySignal == TradeSignal.OpenBuy ? Side.Buy : Side.Sell;
                this.ComputeTradeAction(marketData, targetSide);
                return true;
            }
            
            // Step 2: Determine target side and quantities
            Side reversalSide = currentSide.Value == Side.Buy ? Side.Sell : Side.Buy;
            double flattenQty = netQty;  // Quantity needed to close old position
            double newQty = this.CalculateContractQuantity();  // New position size (usually 1)
            double totalReversalQty = flattenQty + newQty;
            
            // Round to symbol lot size
            totalReversalQty = this.RoundQuantity(totalReversalQty);
            
            if (totalReversalQty < this.Symbol.MinLot)
            {
                AppLog.Error("RowanStrategy", "Reversal", 
                    $"Reversal quantity too small ({totalReversalQty}), aborting");
                return false;
            }
            
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"Reversal calculation: Current={currentSide.Value} {netQty}, " +
                $"Target={reversalSide} {totalReversalQty} (flatten {flattenQty} + open {newQty})");
            
            // Step 3: Risk check (session, max loss - but NOT max open since this is a reversal)
            if (StaticSessionManager.CurrentStatus != Status.Active)
            {
                AppLog.Error("RowanStrategy", "Reversal", 
                    "Session inactive, blocking reversal order");
                return false;
            }
            
            double realizedLoss, floatingLoss;
            double totalLoss = GetSessionLoss(out realizedLoss, out floatingLoss);
            bool maxLossReached = totalLoss < 0 && Math.Abs(totalLoss) >= Math.Abs(_maxSessionLos);
            
            if (maxLossReached)
            {
                AppLog.Error("RowanStrategy", "Reversal", 
                    $"Max session loss reached (${Math.Abs(totalLoss):F2}), blocking reversal");
                return false;
            }
            
            // Step 4: Get old item IDs for cleanup tracking
            var manager = this._manager as TpSlPositionManager;
            var oldItems = manager?.Items
                .OfType<TpSlItemPosition>()
                .Where(item => item.Status != PositionManagerStatus.Closed)
                .Select(item => item.Id)
                .ToList() ?? new List<string>();
            
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"Tracking {oldItems.Count} old item(s) for cleanup");
            
            // Step 5: Prepare market data for SL/TP calculation (for when reversal fills)
            try
            {
                var history = this.HistoryProvider?.HistoricalData;
                if (history != null && history.Count > 1)
                {
                    double rawPivot = reversalSide == Side.Buy
                        ? history[1][PriceType.Low]
                        : history[1][PriceType.High];
                    marketData.SlTriggerPrice = rawPivot;
                }
            }
            catch
            {
                marketData.SlTriggerPrice = reversalSide == Side.Buy ? _cachedPreviousLow : _cachedPreviousHigh;
            }
            
            if (double.IsNaN(marketData.SlTriggerPrice) || marketData.SlTriggerPrice <= 0)
            {
                double cachedPivot = reversalSide == Side.Buy ? _cachedPreviousLow : _cachedPreviousHigh;
                marketData.SlTriggerPrice = cachedPivot;
                if (!double.IsNaN(cachedPivot))
                {
                    AppLog.System("RowanStrategy", "Reversal",
                        $"Using cached pivot {cachedPivot:F2} for reversal SL calculation");
                }
            }
            
            // Step 6: Get market order type
            var marketOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.Order)
                .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Market);
            
            if (marketOrderType == null)
            {
                AppLog.Error("RowanStrategy", "Reversal", "No market order type available, aborting reversal");
                return false;
            }
            
            // Step 7: Generate unique comment for reversal order
            var comment = GenerateComment();
            this.RegistredGuid.Add(comment);
            
            // Step 8: Build and place single reversal market order
            var reversalRequest = new PlaceOrderRequestParameters
            {
                Account = this.Account,
                Symbol = this.Symbol,
                Side = reversalSide,
                Quantity = totalReversalQty,
                OrderTypeId = marketOrderType.Id,
                Comment = $"{comment}.{OrderTypeSubcomment.Entry}",  // Use .Entry so manager tracks it
                TimeInForce = TimeInForce.Day
            };
            
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"═══ PLACING REVERSAL ORDER ═══");
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"Order: {reversalSide} {totalReversalQty} contracts MARKET");
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"Will: Close {flattenQty} {currentSide.Value} + Open {newQty} {reversalSide}");
            
            var result = PlaceOrderWithRetry(reversalRequest, "SingleOrderReversal");
            
            if (result.Status != TradingOperationResultStatus.Success)
            {
                AppLog.Error("RowanStrategy", "Reversal", 
                    $"❌ FAILED to place reversal order: {result.Message}");
                this.RegistredGuid.Remove(comment);
                return false;
            }
            
            // Step 9a: Create manager item for tracking (so manager can bind the new position)
            // Reuse manager variable from Step 4
            if (manager != null)
            {
                manager.CreateItem(comment);
                
                // Step 9b: Plan TEMPORARY SL/TP prices (will be recalculated after position created)
                // These act as placeholders; actual prices will be calculated from fill price
                var slPrices = this.Strategy?.CalculateSl(marketData, reversalSide, marketData.currentPrice);
                var tpPrices = this.Strategy?.CalculateTp(marketData, reversalSide, marketData.currentPrice);
                
                double? plannedSl = null;
                if (slPrices != null && slPrices.Count > 0)
                    plannedSl = this.Symbol.RoundPriceToTickSize(slPrices[0]);
                
                double? plannedTp = null;
                if (tpPrices != null && tpPrices.Count > 0)
                    plannedTp = this.Symbol.RoundPriceToTickSize(tpPrices[0]);
                
                manager.PlanBracket(comment, plannedSl, plannedTp);
                
                AppLog.Trading("RowanStrategy", "Reversal", 
                    $"Manager item created: {comment}, Planned SL={plannedSl:F2}, TP={plannedTp:F2} (will recalc from fill price)");
            }
            
            // Step 10: Create tracker for monitoring fills
            _activeReversal = new ReversalOrderTracker
            {
                OrderId = result.OrderId,
                TargetSide = reversalSide,
                OriginalSide = currentSide.Value,
                OrderQuantity = totalReversalQty,
                FlattenQuantity = flattenQty,
                CumulativeFilled = 0,
                MarketData = marketData,
                InitiatedTime = DateTime.UtcNow,
                OldSlTpCancelled = false,
                NewSlTpPlaced = false,
                OldItemIds = oldItems
            };
            
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"✅ REVERSAL ORDER PLACED: OrderId={result.OrderId}");
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"Monitoring fills: Will cancel old SL/TP after {flattenQty} fills, " +
                $"place new SL/TP after {totalReversalQty} fills");
            AppLog.Trading("RowanStrategy", "Reversal", 
                $"═══════════════════════════════════════════");
            
            return true;
        }

        private void ProcessHistoryUpdate(HistoryEventArgs e, HistoryUpdAteType updateType)
        {
            //TODO: [DEBUG] Review market data payload completeness before trading decisions
            this._logsEntry.Clear();
            this._logsExits.Clear();

            // PHASE 1: CRITICAL - Refresh exposure from broker truth EVERY TICK
            // This must happen before any trading decisions to ensure accurate position counts
            RefreshExposureTracking();
            
            // Periodic deep reconciliation (less frequent, more invasive)
            PeriodicExposureReconcile();

            //TODO: [DEBUG] Detect duplicate indicator registrations triggered by history provider bug

            if (this._atrIndicator.Count == 0)
                this.HistoryProvider.HistoricalData.AddIndicator(this._atrIndicator);
            if (this._deltaBaseIndicator.Count == 0)
                this.HistoryProvider.HistoricalData.AddIndicator(this._deltaBaseIndicator);
            if (this._slipageAtrIndicator.Count == 0)
                this.HistoryProvider.HistoricalData.AddIndicator(this._slipageAtrIndicator);
            if (this._useOffsetEntry != 0 && this._offsetAtrIndicator.Count == 0)
                this.HistoryProvider.HistoricalData.AddIndicator(this._offsetAtrIndicator);

            //TODO: [DEBUG] Guard against unexpected history event payload shapes
            if (e == null)
            {
                AppLog.Error("RowanStrategy", "UpdateCasting", "Rowan Strategy error at Update casting");
                AppLog.Error("RowanStrategy", "StrategyState", "Strategy Will be Disabled");
                this.ForceClosePositions(5);
                this._strategyActive = false;
                return;
            }

            bool isBarClose = updateType == HistoryUpdAteType.NewItem;

            HistoryItem item = (HistoryItem)e.HistoryItem;
            
            // CRITICAL: Update session manager FIRST - must run every Update() regardless of any other logic
            StaticSessionManager.Update(item);
            
            // PHASE 4: Defensive session registration (handles stop/restart)
            // Re-register Trade sessions if cleared
            if (StaticSessionManager.TradeSessions.Count == 0)
            {
                foreach (var sv in InMarketUtc.Build())
                    StaticSessionManager.AddSession(sv, Utils.SessionType.Trade);
            }
            
            // PHASE 4: Re-register Target sessions if cleared (needed for dynamic TP)
            if (StaticSessionManager.TargetSessions.Count == 0)
            {
                foreach (var sv in OffMarketUtc.Build())
                    StaticSessionManager.AddSession(sv, Utils.SessionType.Target);
            }

            // Ensure newly registered sessions have current status
            StaticSessionManager.Update(item);

            // PHASE 3: Continuous tick-level SL/TP discovery and validation
            // Run on EVERY tick (UpdateItem) and bar close (NewItem)
            if (this._manager is TpSlPositionManager discoveryManager)
            {
                var allItems = discoveryManager.Items
                    .OfType<TpSlItemPosition>()
                    .Where(it => it.EntryOrder != null && it.Status != PositionManagerStatus.Closed)
                    .ToList();
                
                foreach (var bundleItem in allItems)
                {
                    try
                    {
                        DateTime nowUtc = DateTime.UtcNow;
                        discoveryManager.EnsureChildOrders(bundleItem);

                        var previousHealth = bundleItem.LastHealthCheckUtc;
                        bundleItem.LastHealthCheckUtc = nowUtc;

                        // PHASE 5: Fast-path discovery using Position.StopLoss/TakeProfit (API docs)
                        // Attempt direct bind from Position if available before scanning orders
                        if (bundleItem.Position != null)
                        {
                            if (string.IsNullOrEmpty(bundleItem.StopLossOrderId) && bundleItem.Position.StopLoss != null)
                            {
                                bundleItem.StopLossOrderId = bundleItem.Position.StopLoss.Id;
                                bundleItem.SlOrderValidated = true;
                                if (!double.IsNaN(bundleItem.Position.StopLoss.TriggerPrice))
                                    bundleItem.ExpectedSlPrice = bundleItem.Position.StopLoss.TriggerPrice;
                                AppLog.System("RowanStrategy", "SlFastPath",
                                    $"✅ Discovered SL via Position.StopLoss for item {bundleItem.Id}");
                            }
                            
                            if (string.IsNullOrEmpty(bundleItem.TakeProfitOrderId) && bundleItem.Position.TakeProfit != null)
                            {
                                bundleItem.TakeProfitOrderId = bundleItem.Position.TakeProfit.Id;
                                bundleItem.TpOrderValidated = true;
                                if (!double.IsNaN(bundleItem.Position.TakeProfit.Price))
                                    bundleItem.ExpectedTpPrice = bundleItem.Position.TakeProfit.Price;
                                AppLog.System("RowanStrategy", "TpFastPath",
                                    $"✅ Discovered TP via Position.TakeProfit for item {bundleItem.Id}");
                            }
                        }

                        var slOrder = bundleItem.GetStopLossOrder(this.Symbol);
                        if (slOrder == null)
                        {
                            if (previousHealth == DateTime.MinValue || (nowUtc - previousHealth) >= ItemHealthLogInterval)
                            {
                                AppLog.System("RowanStrategy", "SlHealth",
                                    $"⚠️ SL still missing for item {bundleItem.Id} (PosId={bundleItem.PositionId ?? "-"})");
                            }
                        }
                        else
                        {
                            double brokerSl = slOrder.TriggerPrice;
                            double expectedSl = bundleItem.ExpectedSlPrice;
                            double diffTicks = (this.Symbol != null && !double.IsNaN(expectedSl))
                                ? this.Symbol.CalculateTicks(expectedSl, brokerSl)
                                : double.NaN;

                            if (double.IsNaN(diffTicks) || Math.Abs(diffTicks) > 0.25)
                            {
                                AppLog.System("RowanStrategy", "SlRecon",
                                    $"Item {bundleItem.Id}: Expected SL={expectedSl:F2}, Broker SL={brokerSl:F2}, Δticks={(double.IsNaN(diffTicks) ? "N/A" : diffTicks.ToString("F2"))}");
                            }
                        }
                        
                        if (slOrder != null)
                        {
                            double tolerance = (this.Symbol?.TickSize ?? 0.25) * SlPriceSyncToleranceMultiplier;
                            if (!double.IsNaN(bundleItem.ExpectedSlPrice) && Math.Abs(slOrder.TriggerPrice - bundleItem.ExpectedSlPrice) > tolerance)
                            {
                                bundleItem.ExpectedSlPrice = slOrder.TriggerPrice;
                                AppLog.System("RowanStrategy", "SlHealth",
                                    $"Resync SL expected price for item {bundleItem.Id}: {bundleItem.ExpectedSlPrice:F2}");
                            }
                        }

                        // Discover missing SL order by price proximity
                        if (string.IsNullOrEmpty(bundleItem.StopLossOrderId) && !double.IsNaN(bundleItem.ExpectedSlPrice))
                        {
                            var proximitySlOrder = Core.Instance.Orders.FirstOrDefault(o =>
                                o.OrderType?.Behavior == OrderTypeBehavior.Stop &&
                                Math.Abs(o.TriggerPrice - bundleItem.ExpectedSlPrice) < this.Symbol.TickSize * 2 &&
                                (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                            );
                            
                            if (proximitySlOrder != null)
                            {
                                bundleItem.StopLossOrderId = proximitySlOrder.Id;
                                bundleItem.SlOrderValidated = true;
                                AppLog.System("TpSlPositionManager", "TickDiscovery",
                                    $"✅ Discovered SL order {proximitySlOrder.Id} at {proximitySlOrder.TriggerPrice:F2} for bundleItem {bundleItem.Id}");
                            }
                        }
                        
                        // Discover missing TP order by price proximity
                        if (string.IsNullOrEmpty(bundleItem.TakeProfitOrderId) && !double.IsNaN(bundleItem.ExpectedTpPrice))
                        {
                            var tpOrder = Core.Instance.Orders.FirstOrDefault(o =>
                                o.OrderType?.Behavior == OrderTypeBehavior.Limit &&
                                Math.Abs(o.Price - bundleItem.ExpectedTpPrice) < this.Symbol.TickSize * 2 &&
                                (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                            );
                            
                            if (tpOrder != null)
                            {
                                bundleItem.TakeProfitOrderId = tpOrder.Id;
                                bundleItem.TpOrderValidated = true;
                                AppLog.System("TpSlPositionManager", "TickDiscovery",
                                    $"✅ Discovered TP order {tpOrder.Id} at {tpOrder.Price:F2} for bundleItem {bundleItem.Id}");
                            }
                        }
                        
                        // Validate SL order still exists and matches expected price
                        if (!string.IsNullOrEmpty(bundleItem.StopLossOrderId) && bundleItem.Position != null)
                        {
                            var trackedSlOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == bundleItem.StopLossOrderId);
                            
                            if (trackedSlOrder == null)
                            {
                                // SL order missing - check if position still active
                                var posStillActive = Core.Instance.Positions.Any(p => p.Id == bundleItem.PositionId);
                                
                                if (posStillActive)
                                {
                                    AppLog.Error("RowanStrategy", "SlValidation",
                                        $"⚠️ CRITICAL: Position {bundleItem.PositionId} active but SL order {bundleItem.StopLossOrderId} missing!");
                                    
                                    // Emergency: close position immediately
                                    try
                                    {
                                        bundleItem.Position?.Close();
                                        AppLog.Trading("RowanStrategy", "SlValidation", 
                                            $"Emergency close sent for position {bundleItem.PositionId}");
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLog.Error("RowanStrategy", "SlValidation", 
                                            $"Emergency close failed: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    // SL filled naturally, clear the ID
                                    bundleItem.StopLossOrderId = null;
                                }
                            }
                            else if (!double.IsNaN(bundleItem.ExpectedSlPrice))
                            {
                                // Verify price matches expectation (detect broker modifications)
                                double priceDiff = Math.Abs(trackedSlOrder.TriggerPrice - bundleItem.ExpectedSlPrice);
                                if (priceDiff > this.Symbol.TickSize)
                                {
                                    AppLog.System("RowanStrategy", "SlValidation",
                                        $"SL price drift: Expected {bundleItem.ExpectedSlPrice:F2}, Actual {trackedSlOrder.TriggerPrice:F2} - syncing");
                                    bundleItem.ExpectedSlPrice = trackedSlOrder.TriggerPrice;  // Sync local state
                                }
                            }
                        }
                        
                        // Validate TP order still exists
                        if (!string.IsNullOrEmpty(bundleItem.TakeProfitOrderId) && bundleItem.Position != null)
                        {
                            var tpOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == bundleItem.TakeProfitOrderId);
                            
                            if (tpOrder == null)
                            {
                                var posStillActive = Core.Instance.Positions.Any(p => p.Id == bundleItem.PositionId);
                                
                                if (posStillActive)
                                {
                                    AppLog.System("RowanStrategy", "TpValidation",
                                        $"⚠️ Position {bundleItem.PositionId} active but TP order {bundleItem.TakeProfitOrderId} missing (likely filled)");
                                }
                                
                                // Clear the ID
                                bundleItem.TakeProfitOrderId = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("RowanStrategy", "TickValidation", 
                            $"Failed to validate bundleItem {bundleItem.Id}: {ex.Message}");
                    }
                }
            }

            // PHASE 2: TICK-BASED SL TRAILING (CRITICAL FIX)
            // This section trails SL on EVERY TICK when position is open and SL exists
            // Previously, SL was never being updated, causing it to remain static
            double currentBarPrice = double.NaN;
            double previousLow = double.NaN;
            double previousHigh = double.NaN;
            double atrInTicks = double.NaN;

            try
            {
                currentBarPrice = item[PriceType.Open];
            }
            catch
            {
                currentBarPrice = double.NaN;
            }

            if (double.IsNaN(currentBarPrice))
            {
                try { currentBarPrice = item[PriceType.Close]; }
                catch { currentBarPrice = double.NaN; }
            }

            if (double.IsNaN(currentBarPrice))
            {
                try { currentBarPrice = item[PriceType.Last]; }
                catch { currentBarPrice = double.NaN; }
            }

            double rawPrevLow = double.NaN;
            double rawPrevHigh = double.NaN;
            try
            {
                var history = this.HistoryProvider?.HistoricalData;
                if (history != null && history.Count > 1)
                {
                    rawPrevLow = history[1][PriceType.Low];
                    rawPrevHigh = history[1][PriceType.High];
                }
            }
            catch
            {
                rawPrevLow = double.NaN;
                rawPrevHigh = double.NaN;
            }

            bool pivotFallbackUsed = false;

            if (!double.IsNaN(rawPrevLow) && rawPrevLow > 0)
            {
                previousLow = rawPrevLow;
                _cachedPreviousLow = rawPrevLow;
                _loggedPivotFallback = false;
            }
            else if (!double.IsNaN(_cachedPreviousLow))
            {
                previousLow = _cachedPreviousLow;
                pivotFallbackUsed = true;
            }
            else
            {
                previousLow = double.NaN;
            }

            if (!double.IsNaN(rawPrevHigh) && rawPrevHigh > 0)
            {
                previousHigh = rawPrevHigh;
                _cachedPreviousHigh = rawPrevHigh;
                _loggedPivotFallback = false;
            }
            else if (!double.IsNaN(_cachedPreviousHigh))
            {
                previousHigh = _cachedPreviousHigh;
                pivotFallbackUsed = true;
            }
            else
            {
                previousHigh = double.NaN;
            }

            if (pivotFallbackUsed && !_loggedPivotFallback)
            {
                AppLog.System("RowanStrategy", "PivotCache",
                    $"Using cached pivot values (High={_cachedPreviousHigh:F2}, Low={_cachedPreviousLow:F2})");
                _loggedPivotFallback = true;
            }

            atrInTicks = double.NaN;
            try
            {
                if (this.Symbol != null && this._slipageAtrIndicator != null)
                {
                    double atrValue = double.NaN;

                    try
                    {
                        if (this._slipageAtrIndicator.Count > 1)
                            atrValue = this._slipageAtrIndicator.GetValue(1);
                        else if (this._slipageAtrIndicator.Count > 0)
                            atrValue = this._slipageAtrIndicator.GetValue();
                    }
                    catch
                    {
                        atrValue = double.NaN;
                    }

                    if (!double.IsNaN(atrValue))
                    {
                        double priceForTicks = currentBarPrice;
                        if (double.IsNaN(priceForTicks))
                        {
                            try { priceForTicks = item[PriceType.Close]; }
                            catch { priceForTicks = double.NaN; }
                        }

                        if (!double.IsNaN(priceForTicks))
                            atrInTicks = Math.Abs(this.Symbol.CalculateTicks(priceForTicks, priceForTicks + atrValue));
                    }
                }
            }
            catch
            {
                atrInTicks = double.NaN;
            }

            if (!double.IsNaN(atrInTicks) && atrInTicks > 0)
            {
                _cachedAtrInTicks = atrInTicks;
                _loggedAtrFallback = false;
            }
            else if (!double.IsNaN(_cachedAtrInTicks))
            {
                atrInTicks = _cachedAtrInTicks;
                if (!_loggedAtrFallback)
                {
                    AppLog.System("RowanStrategy", "AtrCache",
                        $"Using cached ATR ticks {_cachedAtrInTicks:F2}");
                    _loggedAtrFallback = true;
                }
            }

            if (double.IsNaN(atrInTicks) || atrInTicks < 0)
                atrInTicks = 0.0;

            // PHASE 2: Execute SL Trailing on EVERY TICK for all open positions
            if (this._manager is TpSlPositionManager trailingManager && this.Strategy != null && !double.IsNaN(currentBarPrice))
            {
                // Determine SL mode to control update frequency
                var slStrategy = this.Strategy as RowanSlTpStrategy;
                var slMode = slStrategy?.SlModeType ?? SlMode.PreviousCandle;

                var activeItems = trailingManager.Items
                    .OfType<TpSlItemPosition>()
                    .Where(it => it.Status != PositionManagerStatus.Closed &&
                                 (!string.IsNullOrEmpty(it.StopLossOrderId) || !double.IsNaN(it.ExpectedSlPrice)))
                    .ToList();

                foreach (var trailingItem in activeItems)
                {
                    try
                    {
                        var slOrder = trailingItem.GetStopLossOrder(this.Symbol);
                        if (slOrder == null)
                        {
                            if (!double.IsNaN(trailingItem.ExpectedSlPrice) && _slMissingAlerted.Add(trailingItem.Id))
                            {
                                AppLog.System("RowanStrategy", "SlTrailPrereq",
                                    $"Stop-loss missing for item {trailingItem.Id.Substring(0, 8)} (expected {trailingItem.ExpectedSlPrice:F2})");
                            }
                            continue;  // No SL to trail
                        }
                        else
                        {
                            _slMissingAlerted.Remove(trailingItem.Id);
                        }

                        double currentSlPrice = slOrder.TriggerPrice;

                        if (slMode == SlMode.PreviousCandle && !isBarClose)
                        {
                            if (!ShouldTrailPreviousCandleIntrabar(trailingItem, currentBarPrice, currentSlPrice, slStrategy))
                                continue;
                        }

                        // Prepare market data for trailing calculation
                        var marketData = new SlTpData
                        {
                            Symbol = this.Symbol,
                            currentPrice = currentBarPrice,
                            PreviousLow = previousLow,
                            PreviousHigh = previousHigh,
                            AtrInTicks = atrInTicks,
                            SlTriggerPrice = currentSlPrice
                        };

                        // Calculate what the new SL should be
                        var updateSlFunc = this.Strategy.UpdateSl(marketData, trailingItem);
                        double proposedSlPrice = updateSlFunc(currentSlPrice);

                        // Only proceed if SL actually needs to move (favorable direction only)
                        bool shouldUpdate = false;
                        if (trailingItem.Side == Side.Buy && proposedSlPrice > currentSlPrice)
                            shouldUpdate = true;  // Long: SL moves up
                        else if (trailingItem.Side == Side.Sell && proposedSlPrice < currentSlPrice)
                            shouldUpdate = true;  // Short: SL moves down

                        if (shouldUpdate && Math.Abs(proposedSlPrice - currentSlPrice) > this.Symbol.TickSize * 0.1)
                        {
                            // Round to tick size
                            proposedSlPrice = Math.Round(proposedSlPrice / this.Symbol.TickSize) * this.Symbol.TickSize;

                            AppLog.Trading("RowanStrategy", "SlTrailTick",
                                $"Item {trailingItem.Id.Substring(0, 8)}: SL trailing {currentSlPrice:F2} → {proposedSlPrice:F2} (Δ{Math.Abs(this.Symbol.CalculateTicks(currentSlPrice, proposedSlPrice)):F1}t), Price={currentBarPrice:F2}");

                            // Send update to broker via TpSlPositionManager
                            trailingManager.UpdateSl(trailingItem, proposedSlPrice);

                            // Update expected price for tracking
                            trailingItem.ExpectedSlPrice = proposedSlPrice;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("RowanStrategy", "SlTrailTick",
                            $"Failed to trail SL for item {trailingItem.Id}: {ex.Message}");
                    }
                }
            }

            // OLD: Reversal pending execution removed - new system uses TradeAdded monitoring
            
            // Tick-level reversal detection (before trailing or bar-close logic)
            // NEW: Can always attempt reversal - no longer need to check pending state machine
            if (this._strategyActive && this.AllowToTrade && !double.IsNaN(currentBarPrice))
            {
                // Only attempt reversal if not already processing one
                if (_activeReversal == null)
                {
                    var tickEntrySignal = this.CalculateTradeSignal(true, allowLogging: false);
                    var tickExitSignal = this.CalculateTradeSignal(false, allowLogging: false);
                    var tickAction = DetermineTradeAction(tickEntrySignal, tickExitSignal);

                    if (this.ExposeSide == InternalExpositionSide.Both)
                    {
                        AppLog.Error("RowanStrategy", "ExposureCheck", "Detected exposure on both sides during tick evaluation - forcing flat state");
                        this.ForceClosePositions(5);
                    }
                    else if (tickAction == TradeAction.Revert)
                    {
                        var tickMarketData = new SlTpData
                        {
                            Symbol = this.Symbol,
                            currentPrice = currentBarPrice,
                            AtrInTicks = atrInTicks,
                            PreviousLow = previousLow,
                            PreviousHigh = previousHigh
                        };

                        // NEW: Single-order reversal (no state machine, no delays)
                        if (ExecuteSingleOrderReversal(tickMarketData, tickEntrySignal, tickExitSignal))
                            return;
                    }
                }
            }

            if (!isBarClose && this.Strategy is RowanSlTpStrategy tickTrailStrategy && tickTrailStrategy.SlModeType == SlMode.AtrTrailing)
            {
                try
                {
                    this.UpdateSlTp(new SlTpData
                    {
                        Symbol = this.Symbol,
                        currentPrice = currentBarPrice,
                        AtrInTicks = atrInTicks,
                        PreviousLow = previousLow,
                        PreviousHigh = previousHigh
                    }, isSl: true);
                }
                catch (Exception ex)
                {
                    AppLog.Error("RowanStrategy", "TickTrailing", $"Failed to update trailing SL on tick: {ex.Message}");
                }
            }

            // PHASE 4: TRAILING
            // Mode 0 (PreviousCandle): Trail at bar close only
            // Mode 2 (AtrTrailing): Trail every tick
            if (this.Strategy is RowanSlTpStrategy trailStrategy && this._manager is TpSlPositionManager trailManager)
            {
                bool shouldTrailNow = false;
                
                if (trailStrategy.SlModeType == SlMode.AtrTrailing)
                {
                    // Mode 2: Trail on every update (tick)
                    shouldTrailNow = true;
                }
                else if (trailStrategy.SlModeType == SlMode.PreviousCandle && isBarClose)
                {
                    // Mode 0: Trail only at bar close
                    shouldTrailNow = true;
                }
                
                if (shouldTrailNow && !double.IsNaN(currentBarPrice))
                {
                    var activeItems = trailManager.Items
                        .OfType<TpSlItemPosition>()
                        .Where(it => it.Position != null && 
                                    it.Status != PositionManagerStatus.Closed && 
                                    it.Status != PositionManagerStatus.Aborted)
                        .ToList();

                    if (activeItems.Count > 0)
                    {
                        var trailingData = new SlTpData
                        {
                            Symbol = this.Symbol,
                            currentPrice = currentBarPrice,
                            AtrInTicks = atrInTicks,
                            PreviousLow = previousLow,
                            PreviousHigh = previousHigh
                        };

                        foreach (var activeItem in activeItems)
                        {
                            try
                            {
                                var updateDelegate = trailStrategy.UpdateSl(trailingData, activeItem);
                                if (updateDelegate != null)
                                {
                                    // PREVIEW: where SL is supposed to be vs where it actually is
                                    double currentSl = double.NaN;
                                    try
                                    {
                                        currentSl = activeItem.GetStopLossOrder(this.Symbol)?.TriggerPrice ?? activeItem.ExpectedSlPrice;
                                    }
                                    catch { currentSl = activeItem.ExpectedSlPrice; }

                                    double proposedSl = double.NaN;
                                    try { proposedSl = updateDelegate(currentSl); } catch { /* ignore preview errors */ }

                                    if (!double.IsNaN(currentSl) && !double.IsNaN(proposedSl))
                                    {
                                        AppLog.Trading("RowanStrategy", "SlPreview",
                                            $"[Item {activeItem.Id}] SL preview → current={currentSl:F2}, proposed={proposedSl:F2}, price={trailingData.currentPrice:F2}");
                                    }

                                    trailManager.UpdateSl(activeItem, updateDelegate);
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLog.Error("RowanStrategy", "TrailingSL", 
                                    $"Failed to update SL for bundleItem {activeItem.Id}: {ex.Message}");
                            }
                        }
                    }
                }
            }

            // PHASE 4: CRITICAL - Stale limit cancellation on bar close (1 bar timeout)
            if (_pendingEntry != null && isBarClose)
            {
                int currentBarIndex = this.HistoryProvider?.HistoricalData?.Count ?? 0;
                
                // Check if limit order is stale (placed on previous bar)
                if (!string.IsNullOrEmpty(_pendingEntry.LimitOrderId) && 
                    currentBarIndex > _pendingEntry.PlacementBarIndex)
                {
                    var limitOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == _pendingEntry.LimitOrderId);
                    
                    if (limitOrder != null && 
                        (limitOrder.Status == OrderStatus.Opened || limitOrder.Status == OrderStatus.PartiallyFilled))
                    {
                        AppLog.Trading("RowanStrategy", "StaleLimitCancel",
                            $"Cancelling unfilled limit {limitOrder.Id} at {limitOrder.Price:F2} (placed bar {_pendingEntry.PlacementBarIndex}, now {currentBarIndex})");
                        ClearPendingEntry("Limit timeout - bar closed without fill");
                    }
                    else if (limitOrder == null || limitOrder.Status == OrderStatus.Filled)
                    {
                        // Order filled or already gone - just clear pending
                        AppLog.System("RowanStrategy", "LimitFilled",
                            $"Limit order {_pendingEntry.LimitOrderId} filled or removed, clearing pending");
                        _pendingEntry = null;
                    }
                }
            }

            // Handle pending entries for offset entry (market order mode)
            // Check on every update (ticks) when in market order mode
            if (this._useOffsetEntry != 0 && this._useOffsetLimitOrders == 0 && _pendingEntry != null)
            {
                double currentPrice = item[PriceType.Last];
                bool shouldExecute = false;
                
                if (_pendingEntry.Side == Side.Buy)
                {
                    // Long: execute when price drops to or below target
                    shouldExecute = currentPrice <= _pendingEntry.TargetPrice;
                }
                else
                {
                    // Short: execute when price rises to or above target
                    shouldExecute = currentPrice >= _pendingEntry.TargetPrice;
                }
                
                if (shouldExecute)
                {
                    AppLog.Trading("RowanStrategy", "OffsetEntry", 
                        $"Executing pending {_pendingEntry.Side} entry at {currentPrice}, Target was {_pendingEntry.TargetPrice}");
                    
                    // PHASE 2: Risk gate before offset-market execute
                    if (!PreTradeRiskCheck("OffsetMarketExecute"))
                    {
                        AppLog.Trading("RowanStrategy", "OffsetEntry", "Offset market execute blocked by risk gate");
                        _pendingEntry = null;
                        return;
                    }
                    
                    this.ComputeTradeAction(_pendingEntry.MarketData, _pendingEntry.Side);
                    _pendingEntry = null;
                }
            }

            if (!isBarClose)
                return;

            if (this._loadAsync && !this.HistoryProvider.VolumeDataReady) 
                return;
            try
            {
                if (!this.AllowToTrade)
                {
                    double realizedLoss;
                    double floatingLoss;
                    double totalLoss = GetSessionLoss(out realizedLoss, out floatingLoss);
                    bool maxLossReached = totalLoss < 0 && Math.Abs(totalLoss) >= Math.Abs(_maxSessionLos);
                    bool sessionActive = StaticSessionManager.CurrentStatus == Status.Active;

                    // NEW: Check if reversal is in progress (simpler than old system)
                    bool reversalInProgress = (_activeReversal != null);

                    if (reversalInProgress && !sessionActive && !maxLossReached)
                    {
                        AppLog.System("RowanStrategy", "SessionGuard",
                            "Session inactive but reversal in progress - applying grace window");
                    }
                    else
                    {
                        // Clear pending entry when trading not allowed
                        ClearPendingEntry("Trading not allowed - session inactive or max loss reached");

                        if (sessionActive && maxLossReached && !_sessionClosed)
                        {
                            double realizedDraw = realizedLoss < 0 ? -realizedLoss : 0;
                            double floatingDraw = floatingLoss < 0 ? -floatingLoss : 0;
                            double totalDraw = totalLoss < 0 ? -totalLoss : 0;

                            AppLog.Trading("RowanStrategy", "SessionGuard",
                                $"Max session loss reached (realized={realizedDraw:F2}, floating={floatingDraw:F2}, total={totalDraw:F2})");
                            this.ForceClosePositions(5);
                            _sessionClosed = true;
                        }

                        return;
                    }
                }
                
                // OLD: Reversal timeout logic removed - new system handles timeouts via TradeAdded monitoring
                
            //TODO: [DEBUG] Validate ATR-based slippage computation for entry price

            //TODO: [DEBUG] Double-check history index offsets when reading price levels
            double effectivePrice = currentBarPrice;
            if (double.IsNaN(effectivePrice))
            {
                try { effectivePrice = item[PriceType.Open]; }
                catch
                {
                    try { effectivePrice = item[PriceType.Close]; }
            catch
            {
                        try { effectivePrice = item[PriceType.Last]; }
                        catch { effectivePrice = 0.0; }
                    }
                }
            }

            var marketData = new SlTpData
            {
                    Symbol = this.Symbol,
                currentPrice = effectivePrice,
                AtrInTicks = atrInTicks,
                PreviousLow = previousLow,
                PreviousHigh = previousHigh,
                };
                TradeSignal entry_signal = this.CalculateTradeSignal(true);
                TradeSignal exit_signal = this.CalculateTradeSignal(false);
                TradeAction action = DetermineTradeAction(entry_signal, exit_signal);

                if (this.ExposeSide == InternalExpositionSide.Both)
                {
                        AppLog.Error("RowanStrategy", "ExposureCheck", "Exposed on Both Sides positions");
                        this.ForceClosePositions(5);
                    action = TradeAction.Wait;
                }

                if (this._lotQuantity > 0)
                    this.OverrideQuantity(this.SetQuantity());

                if (!_strategyActive)
                    return;

                //TODO: [DEBUG] Confirm entry sizing respects max exposure thresholds

                // PHASE 1: EXIT-FIRST PRIORITY - Handle exits before any entries
                // Close signal: market-close all positions, clear SL/TP, return
                if (action == TradeAction.Close)
                {
                    ClearPendingEntry("Close signal");
                    bool res = this.ForceClosePositions(5);
                    
                    if (res)
                    {
                        AppLog.Trading("RowanStrategy", "TradeSignal", $"ALL POSITIONS CLOSED EntrySignal={entry_signal}, ExitSignal={exit_signal}, Action={action}");
                        foreach (var logsitem in this._logsEntry)
                            AppLog.Trading("RowanStrategy", "EntryDetails", $"Entry Details: {logsitem}");
                        foreach (var logsExitem in this._logsExits)
                            AppLog.Trading("RowanStrategy", "ExitDetails", $"Exit Details: {logsExitem}");
                    }
                    else
                    {
                        AppLog.Error("RowanStrategy", "TradeSignal", $"FAILED TO CLOSE POSITIONS, STRATEGY STOPPED EntrySignal={entry_signal}, ExitSignal={exit_signal}, Action={action}");
                        foreach (var logsitem in this._logsEntry)
                            AppLog.Trading("RowanStrategy", "EntryDetails", $"Entry Details: {logsitem}");
                        foreach (var logsExitem in this._logsExits)
                            AppLog.Trading("RowanStrategy", "ExitDetails", $"Exit Details: {logsExitem}");
                        this._strategyActive = false;
                    }
                    return;  // Exit-first: no further processing
                }

                // Revert signal: execute single-order reversal
                if (action == TradeAction.Revert)
                {
                    // NEW: Single market order that closes old + opens new in one transaction
                    if (ExecuteSingleOrderReversal(marketData, entry_signal, exit_signal))
                        return;
                }

                // PHASE 2: ENTRY HANDLING - Only reached if not exiting
                if (action == TradeAction.Buy || action == TradeAction.Sell)
                {
                    if (ExposedCount >= this._maxOpen)
                    {
                        AppLog.Trading("RowanStrategy", "TradeSignal", $"AVOIDED DUE MAX EXPO REACHED");
                        AppLog.Trading("RowanStrategy", "TradeSignalContext", $"EntrySignal={entry_signal}, ExitSignal={exit_signal}, Action={action}");
                        foreach (var logsitem in this._logsEntry)
                            AppLog.Trading("RowanStrategy", "EntryDetails", $"Entry Details: {logsitem}");
                        foreach (var logsExitem in this._logsExits)
                            AppLog.Trading("RowanStrategy", "ExitDetails", $"Exit Details: {logsExitem}");
                    }
                    else
                    {
                        Side entrySide = action == TradeAction.Buy ? Side.Buy : Side.Sell;
                        
                        // Exposure will be refreshed by RefreshExposureTracking() at next Update()
                        
                        // Set SL trigger price
                        marketData.SlTriggerPrice = entrySide == Side.Buy 
                            ? this.HistoryProvider.HistoricalData[1][PriceType.Low]
                            : this.HistoryProvider.HistoricalData[1][PriceType.High];
                        
                        // PHASE 2: Risk gate before entry
                        if (!PreTradeRiskCheck("ImmediateEntry"))
                        {
                            AppLog.Trading("RowanStrategy", "TradeSignal", $"Entry blocked by risk gate");
                            return;
                        }
                        
                        // Check if offset entry is enabled
                        if (this._useOffsetEntry != 0)
                        {
                            // Clear any existing pending entry
                            ClearPendingEntry("New signal");
                            
                            double targetPrice = CalculateOffsetEntryPrice(entrySide, marketData.currentPrice);
                            
                            // Store pending entry
                            _pendingEntry = new PendingEntry
                            {
                                Side = entrySide,
                                TargetPrice = targetPrice,
                                OpenPrice = marketData.currentPrice,
                                MarketData = marketData,
                                SignalTime = DateTime.UtcNow,
                                IsReversal = false
                            };
                            
                            AppLog.Trading("RowanStrategy", "TradeSignal", 
                                $"Offset entry scheduled: {entry_signal}, Target={targetPrice}");
                            
                            // If limit mode, place limit order immediately
                            if (this._useOffsetLimitOrders != 0)
                            {
                                PlaceLimitOffsetEntry(entrySide, targetPrice, marketData);
                            }
                            else
                            {
                                AppLog.Trading("RowanStrategy", "TradeSignal", 
                                    "Market mode: waiting for price to reach target");
                            }
                        }
                        else
                        {
                            // Original immediate entry logic
                            this.ComputeTradeAction(marketData, entrySide);
                        }
                        
                        // Existing logging
                        AppLog.Trading("RowanStrategy", "TradeSignal", $"EntrySignal={entry_signal}, ExitSignal={exit_signal}, Action={action}");
                        foreach (var logsitem in this._logsEntry)
                            AppLog.Trading("RowanStrategy", "EntryDetails", $"Entry Details: {logsitem}");
                        foreach (var logsExitem in this._logsExits)
                            AppLog.Trading("RowanStrategy", "ExitDetails", $"Exit Details: {logsExitem}");
                    }
                }

                // PHASE 3: SL TRAILING - Only if not exiting/entering
                // (Close/Revert already returned above; Buy/Sell handled in previous block)
                else
                {
                    try
                    {
                        //TODO: [DEBUG] Track SL/TP adjustments for consistency with live positions

                        //🧠 HINT: [Non agiamo su segnali differenti]

                        this.UpdateSlTp(marketData, isSl: true);

                    }
                    catch { AppLog.Error("RowanStrategy", "UpdateSl", "Failed to update Sl"); }
                }

                if (this._verbosityFreqCount <= this._verbosityFreq)
                {
                    this._verbosityFreqCount++;

                    if (this._verbosityFreqCount == this._verbosityFreq)
                    {
                        AppLog.System("RowanStrategy", "VerboseStatus", $"Strategy status: SessionActive={StaticSessionManager.CurrentStatus}, AllowToTrade={this.AllowToTrade}");
                        AppLog.System("RowanStrategy", "VerboseExposure", $"ExposedSide={this.Metrics.ExposedSide}, ExposedCount={this.Metrics.ExposedCount}");
                        AppLog.System("RowanStrategy", "VerboseSignals", $"EntrySignal={entry_signal}, ExitSignal={exit_signal}, Action={action}");
                        foreach (var logsitem in this._logsEntry)
                            AppLog.System("RowanStrategy", "VerboseEntryDetails", $"Entry Details: {logsitem}");
                        foreach (var logsExitem in this._logsExits)
                            AppLog.System("RowanStrategy", "VerboseExitDetails", $"Exit Details: {logsExitem}");
                        this._verbosityFreqCount = 0;
                    }
                }

            }
            catch (Exception ex)
            {

                //TODO: [DEBUG] Escalate unexpected update exceptions with enriched diagnostics
                AppLog.Error("RowanStrategy", "Update", $"Rowan Strategy error at Update with message : {ex.Message}");
                throw;
            }
        }

        public override void Update(object obj)
        {
            this.ProcessHistoryUpdate(obj as HistoryEventArgs, HistoryUpdAteType.NewItem);
        }

        protected override void OnHistoryUpdate(HistoryEventArgs e, HistoryUpdAteType updateType)
        {
            this.ProcessHistoryUpdate(e, updateType);
        }

        private bool ShouldTrailPreviousCandleIntrabar(TpSlItemPosition item, double currentPrice, double currentSl, RowanSlTpStrategy slStrategy)
        {
            if (slStrategy == null || this.Symbol == null || double.IsNaN(currentPrice) || double.IsNaN(currentSl))
                return false;

            bool priceAhead = item.Side == Side.Buy
                ? currentPrice > currentSl
                : currentPrice < currentSl;

            if (!priceAhead)
                return false;

            int thresholdTicks = Math.Max(1, slStrategy.min_slInTicks / 2);
            double ticksFromStop = Math.Abs(this.Symbol.CalculateTicks(currentSl, currentPrice));

            return ticksFromStop >= thresholdTicks;
        }

        private void ComputeTradeAction(SlTpData data, Side side ) => this.Trade(side, data.currentPrice, data, data);

        //TODO: [DEBUG] Validate forced close routine ensures manager state consistency
        private bool ForceClosePositions(int max_attempt)
        {
            try
            {
                // Cancel all unfilled orders first (including OCO children)
                CancelAllUnfilledOrders("Force close");

                var manager = this._manager as TpSlPositionManager;
                var activeItems = manager?.Items
                    .OfType<TpSlItemPosition>()
                    .Where(item => item.Status != PositionManagerStatus.Closed)
                    .ToList() ?? new List<TpSlItemPosition>();

                AppLog.Trading("RowanStrategy", "ForceClose", $"Closing {activeItems.Count} tracked item(s)");

                foreach (var item in activeItems)
                {
                    try
                    {
                        item.Quit();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("RowanStrategy", "ForceClose", $"Quit failed for item {item.Id}: {ex.Message}");
                    }
                }

                // Fallback: directly close any remaining broker positions matching our instrument
                var brokerPositions = Core.Instance.Positions
                    .Where(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol))
                    .ToList();

                foreach (var pos in brokerPositions)
                {
                    try
                    {
                        pos.Close();
                        AppLog.System("RowanStrategy", "ForceClose", $"Sent close request for broker position {pos.Id}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("RowanStrategy", "ForceClose", $"Broker close failed for {pos.Id}: {ex.Message}");
                    }
                }
 
                // PHASE 5: Poll until all three conditions met: positions closed, orders cancelled, manager flat
                for (int i = 0; i < Math.Max(1, max_attempt); i++)
                {
                    // Give broker time to process
                    if (i > 0)
                        System.Threading.Thread.Sleep(500);
                    
                    // Check 1: Broker positions
                    bool brokerFlat = !Core.Instance.Positions
                        .Any(p => AccountMatches(p.Account) && SymbolsMatch(p.Symbol));
                    
                    // Check 2: Manager items
                    bool managerFlat = manager == null || !manager.Items
                        .OfType<TpSlItemPosition>()
                        .Any(item => item.Status != PositionManagerStatus.Closed);
                    
                    // Check 3: Outstanding orders (including orphaned SL/TP)
                    bool ordersCleared = !Core.Instance.Orders
                        .Any(o => SymbolsMatch(o.Symbol) && AccountMatches(o.Account) &&
                                 (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled));
                    
                    if (brokerFlat && managerFlat && ordersCleared)
                    {
                        manager?.PruneOrphanedItems();
                        EnsureExposureConsistency(true);
                        AppLog.Trading("RowanStrategy", "ForceClose", 
                            $"✅ Confirmed flat after {i+1} attempt(s): Broker={brokerFlat}, Manager={managerFlat}, Orders={ordersCleared}");
                        _slMissingAlerted.Clear();
                        return true;
                    }

                    AppLog.System("RowanStrategy", "ForceClose", 
                        $"Poll {i+1}/{max_attempt}: Broker={!brokerFlat}, Manager={!managerFlat}, Orders={!ordersCleared}, waiting 500ms");
                }
 
                AppLog.Error("RowanStrategy", "ForceClose", 
                    $"Failed to achieve flatness after {max_attempt} attempts");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("RowanStrategy", "ForceClose", $"Unexpected error during force close: {ex.Message}");
                return false;
            }
        }

        private TradeSignal CalculateTradeSignal(bool entrySign, bool allowLogging = true)
        {
            //TODO: [DEBUG] Validate signal tallies align with configured thresholds
            List<string> activeNames = entrySign ? _entryLineNames : _exitLineNames;

            int longCount = 0, shortCount = 0;

            void Tally(double v, ref int pos, ref int neg)
            {
                if (v > 0) pos++;
                else if (v < 0) neg++;
            }
            foreach (var name in activeNames)
            {
                var v = GetLineValueByNameAny(name, entrySign, allowLogging);
                Tally(v, ref longCount, ref shortCount);
            }

            // Emit tally summary for diagnostics
            AppLog.System("RowanStrategy", entrySign ? "EntryTally" : "ExitTally",
                $"long={longCount}, short={shortCount}, min={(entrySign ? _entryMinConditions : _exitMinConditions)}");

            switch (entrySign)
            {
                case true:
                    if (longCount > shortCount)
                        if (longCount >= _entryMinConditions)
                            return TradeSignal.OpenBuy;
                        else
                            return TradeSignal.Wait;
                    else if (longCount < shortCount)
                        if (shortCount >= _entryMinConditions)
                            return TradeSignal.OpenSell;
                        else
                            return TradeSignal.Wait;
                    else
                            return TradeSignal.Wait;

                case false:
                    if (longCount > shortCount)
                        if (longCount >= _exitMinConditions)
                            return TradeSignal.CloseSell;
                        else
                            return TradeSignal.Wait;
                    else if (longCount < shortCount)
                        if (shortCount >= _exitMinConditions)
                            return TradeSignal.CloseLong;
                        else
                            return TradeSignal.Wait;
                    else
                        return TradeSignal.Wait;
            }
        }

        private TradeAction DetermineTradeAction(TradeSignal entrySignal, TradeSignal exitSignal)
        {
            switch (this.ExposeSide)
            {
                case InternalExpositionSide.Long:
                    if (entrySignal == TradeSignal.OpenSell)
                        return TradeAction.Revert;
                    if (exitSignal == TradeSignal.CloseLong)
                        return TradeAction.Close;
                    if (entrySignal == TradeSignal.OpenBuy)
                        return TradeAction.Buy;
                    return TradeAction.Wait;

                case InternalExpositionSide.Short:
                    if (entrySignal == TradeSignal.OpenBuy)
                        return TradeAction.Revert;
                    if (exitSignal == TradeSignal.CloseSell)
                        return TradeAction.Close;
                    if (entrySignal == TradeSignal.OpenSell)
                        return TradeAction.Sell;
                    return TradeAction.Wait;

                case InternalExpositionSide.Unexposed:
                    if (entrySignal == TradeSignal.OpenBuy)
                        return TradeAction.Buy;
                    if (entrySignal == TradeSignal.OpenSell)
                        return TradeAction.Sell;
                    return TradeAction.Wait;

                case InternalExpositionSide.Both:
                    return TradeAction.Close;

                default:
                    return TradeAction.Wait;
            }
        }

        // OLD REVERSAL METHODS REMOVED - Now using ExecuteSingleOrderReversal()
        // The old complex state machine (ExecuteReversal, ExecuteDelayedReversalEntry, LogReversalStage)
        // has been completely replaced with simple single-order reversal

        private double CalculateOffsetEntryPrice(Side side, double openPrice)
        {
            // Get ATR value in price units
            double atrValue = this._offsetAtrIndicator?.GetValue(1) ?? 0.0;
            
            if (atrValue <= 0 || double.IsNaN(atrValue) || double.IsInfinity(atrValue))
            {
                AppLog.Error("RowanStrategy", "OffsetCalc", "Invalid ATR for offset calculation, using open price");
                return openPrice;
            }
            
            // Calculate offset in ticks
            double offsetInTicks = Math.Abs(this.Symbol.CalculateTicks(
                atrValue + openPrice, openPrice)) * this._offsetAtrMultiplier;
            
            // Apply offset (subtract for long, add for short)
            double targetPrice = side == Side.Buy
                ? this.Symbol.CalculatePrice(openPrice, -offsetInTicks)
                : this.Symbol.CalculatePrice(openPrice, offsetInTicks);
            
            AppLog.Trading("RowanStrategy", "OffsetCalc", 
                $"Calculated offset entry: Side={side}, Open={openPrice}, ATR={atrValue:F2}, " +
                $"Offset={offsetInTicks:F1} ticks, Target={targetPrice}");
            
            // Round to tick size before returning
            return this.Symbol.RoundPriceToTickSize(targetPrice);
        }

        // PHASE A/B: Preflight and OMS classify/retry for entry orders placed by this strategy (non-close orders)
        private void PreflightOrder(PlaceOrderRequestParameters req)
        {
            if (req == null || this.Symbol == null)
                return;

            // Tick rounding and field semantics
            var allowed = req.OrderTypeId;
            var orderType = this.Symbol.GetAlowedOrderTypes(OrderTypeUsage.Order)?.FirstOrDefault(o => o.Id == allowed);

            if (orderType != null)
            {
                if (orderType.Behavior == OrderTypeBehavior.Market)
                {
                    // Market: no price fields
                    req.Price = double.NaN;
                    req.TriggerPrice = double.NaN;
                }
                else if (orderType.Behavior == OrderTypeBehavior.Limit)
                {
                    // Limit: Price only
                    if (!double.IsNaN(req.Price)) req.Price = this.Symbol.RoundPriceToTickSize(req.Price);
                    req.TriggerPrice = double.NaN;
                }
                else if (orderType.Behavior == OrderTypeBehavior.Stop)
                {
                    // Stop: Trigger only
                    if (!double.IsNaN(req.TriggerPrice)) req.TriggerPrice = this.Symbol.RoundPriceToTickSize(req.TriggerPrice);
                    req.Price = double.NaN;
                }
            }
        }

        private TradingOperationResult PlaceOrderWithRetry(PlaceOrderRequestParameters req, string context)
        {
            try
            {
                PreflightOrder(req);
                var result = Core.Instance.PlaceOrder(req);
                if (result.Status == TradingOperationResultStatus.Success)
                    return result;

                string msg = result.Message ?? "unknown";
                AppLog.Error("RowanStrategy", "OmsRefuse", $"{context}: Refuse: {msg} req: beh? posId={req.PositionId ?? "-"} price={req.Price} trig={req.TriggerPrice}");

                // Single corrective retry paths
                bool retried = false;

                // Off-tick/increment → re-round then retry
                if (msg.IndexOf("tick", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("increment", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!double.IsNaN(req.Price)) req.Price = this.Symbol.RoundPriceToTickSize(req.Price);
                    if (!double.IsNaN(req.TriggerPrice)) req.TriggerPrice = this.Symbol.RoundPriceToTickSize(req.TriggerPrice);
                    retried = true;
                }

                // Unsupported parameter → drop additional and retry
                if (!retried && (msg.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("reduce", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (req.AdditionalParameters != null)
                        req.AdditionalParameters.Clear();
                    retried = true;
                }

                if (retried)
                {
                    var r2 = Core.Instance.PlaceOrder(req);
                    if (r2.Status != TradingOperationResultStatus.Success)
                        AppLog.Error("RowanStrategy", "OmsRefuse", $"{context}: retry failed: {r2.Message}");
                    return r2;
                }

                return result;
            }
            catch (Exception ex)
            {
                AppLog.Error("RowanStrategy", "OmsRefuse", $"{context}: exception during place: {ex.Message}");
                throw;
            }
        }

        private void PlaceLimitOffsetEntry(Side side, double targetPrice, SlTpData marketData, bool isReversal = false)
        {
            // PRE-FLIGHT CHECK: Session active?
            if (StaticSessionManager.CurrentStatus != Status.Active)
            {
                AppLog.Trading("RowanStrategy", "OffsetEntry", "Order blocked: Not in trading session");
                return;
            }
            
            // PRE-FLIGHT CHECK: Risk limits
            if (!PreTradeRiskCheck("PlaceLimitOffsetEntry"))
                return;
            
            try
            {
                var comment = GenerateComment();
                this.RegistredGuid.Add(comment);
                
                // For futures (NQ/ES): use unified contract sizing
                double quantity = this.CalculateContractQuantity();
                
                var limitOrderType = Symbol.GetAlowedOrderTypes(OrderTypeUsage.Order)
                    .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Limit);
                
                if (limitOrderType == null)
                {
                    AppLog.Error("RowanStrategy", "OffsetEntry", "Limit orders not supported, falling back to market");
                    this.ComputeTradeAction(marketData, side);
                    return;
                }
                
                // Round entry price to tick size (ES/NQ = 0.25)
                double roundedPrice = this.Symbol.RoundPriceToTickSize(targetPrice);
                
                // PHASE 1: Limit order hygiene - set Price only (no TriggerPrice) per API docs
                var orderReq = new PlaceOrderRequestParameters
                {
                    Account = this.Account,
                    Symbol = this.Symbol,
                    Side = side,
                    Quantity = quantity,
                    Price = roundedPrice,  // Limit: price only
                    OrderTypeId = limitOrderType.Id,
                    Comment = $"{comment}.{OrderTypeSubcomment.Entry}"
                };
                
                var sl = Strategy.CalculateSl(marketData, side, roundedPrice);
                var tp = Strategy.CalculateTp(marketData, side, roundedPrice);

                double? plannedSl = null;
                if (sl != null && sl.Count > 0)
                    plannedSl = this.Symbol.RoundPriceToTickSize(sl[0]);

                double? plannedTp = null;
                if (tp != null && tp.Count > 0)
                    plannedTp = this.Symbol.RoundPriceToTickSize(tp[0]);

                this._manager?.PlanBracket(comment, plannedSl, plannedTp);
                
                var result = PlaceOrderWithRetry(orderReq, "OffsetLimitEntry");
                
                if (result.Status == TradingOperationResultStatus.Success)
                {
                    _pendingEntry.LimitOrderId = result.OrderId;
                    _pendingEntry.PlacementBarIndex = this.HistoryProvider?.HistoricalData?.Count ?? 0;
                    
                    AppLog.Trading("RowanStrategy", "LimitPlaced", 
                        $"Limit {side} order {result.OrderId} placed at {roundedPrice}, bar={_pendingEntry.PlacementBarIndex}");
                }
                else
                {
                    AppLog.Error("RowanStrategy", "OffsetEntry", 
                        $"Failed to place limit order: {result.Message}");
                    _pendingEntry = null;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("RowanStrategy", "OffsetEntry", $"Error placing limit order: {ex.Message}");
                _pendingEntry = null;
            }
        }

        private void ClearPendingEntry(string reason)
        {
            if (_pendingEntry != null)
            {
                AppLog.Trading("RowanStrategy", "OffsetEntry", 
                    $"Clearing pending {_pendingEntry.Side} entry. Reason: {reason}");
                
                // Cancel limit order if one was placed
                if (!string.IsNullOrEmpty(_pendingEntry.LimitOrderId))
                {
                    var order = Core.Instance.Orders.FirstOrDefault(o => o.Id == _pendingEntry.LimitOrderId);
                    if (order != null && (order.Status == OrderStatus.Opened || order.Status == OrderStatus.PartiallyFilled))
                    {
                        // CRITICAL: Also cancel associated OCO bracket orders (SL/TP)
                        string groupId = order.GroupId;
                        order.Cancel();
                        
                        if (!string.IsNullOrEmpty(groupId))
                        {
                            // Handle composite GroupIds with token INTERSECTION
                            var entryTokens = groupId.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim())
                                .Where(t => !string.IsNullOrEmpty(t))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            
                            var ocoOrders = Core.Instance.Orders
                                .Where(o =>
                                {
                                    if (string.IsNullOrEmpty(o.GroupId)) return false;
                                    if (o.Id == order.Id) return false;
                                    if (o.Status != OrderStatus.Opened && o.Status != OrderStatus.PartiallyFilled) return false;
                                    
                                    var orderTokens = o.GroupId.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(s => s.Trim())
                                        .Where(t => !string.IsNullOrEmpty(t));
                                    
                                    return orderTokens.Any(t => entryTokens.Contains(t));
                                })
                                .ToList();
                            
                            foreach (var ocoOrder in ocoOrders)
                            {
                                try
                                {
                                    ocoOrder.Cancel();
                                    AppLog.System("RowanStrategy", "OffsetEntry", 
                                        $"Cancelled OCO order: {ocoOrder.OrderType?.Behavior} @ {ocoOrder.TriggerPrice} (GroupId={ocoOrder.GroupId})");
                                }
                                catch (Exception ex)
                                {
                                    AppLog.Error("RowanStrategy", "OffsetEntry", 
                                        $"Failed to cancel OCO order {ocoOrder.Id}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                
                _pendingEntry = null;
            }
        }
        
        /// <summary>
        /// PHASE 3: Cancel only THIS STRATEGY's unfilled orders (scoped cancellation)
        /// Restricts to orders belonging to this strategy via Comment/GroupId/PositionId matching
        /// </summary>
        private void CancelAllUnfilledOrders(string reason)
        {
            // Get our tracked entry GroupId tokens for scoping
            var manager = this._manager as TpSlPositionManager;
            var ourEntryGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ourPositionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            if (manager != null)
            {
                foreach (var item in manager.Items.OfType<TpSlItemPosition>())
                {
                    // Collect entry order GroupId tokens
                    if (!string.IsNullOrEmpty(item.EntryOrder?.GroupId))
                    {
                        foreach (var token in item.EntryOrder.GroupId.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var trimmed = token.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                                ourEntryGroupIds.Add(trimmed);
                        }
                    }
                    
                    // Collect position IDs
                    if (!string.IsNullOrEmpty(item.PositionId))
                        ourPositionIds.Add(item.PositionId);
                }
            }
            
            // Filter to only OUR orders
            var unfilledOrders = Core.Instance.Orders
                .Where(o => o.Symbol == this.Symbol && o.Account == this.Account &&
                           (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled))
                .Where(o =>
                {
                    // Match by Comment prefix (our GUID base)
                    if (!string.IsNullOrEmpty(o.Comment) && o.Comment.StartsWith(this.StrategyName + "_", StringComparison.OrdinalIgnoreCase))
                        return true;
                    
                    // Match by GroupId token intersection
                    if (!string.IsNullOrEmpty(o.GroupId) && ourEntryGroupIds.Count > 0)
                    {
                        var orderTokens = o.GroupId.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => t.Trim())
                            .Where(t => !string.IsNullOrEmpty(t));
                        
                        if (orderTokens.Any(t => ourEntryGroupIds.Contains(t)))
                            return true;
                    }
                    
                    // Match by PositionId
                    if (!string.IsNullOrEmpty(o.PositionId) && ourPositionIds.Contains(o.PositionId))
                        return true;
                    
                    return false;
                })
                .ToList();
            
            if (unfilledOrders.Any())
            {
                AppLog.Trading("RowanStrategy", "CancelAll", 
                    $"Cancelling {unfilledOrders.Count} unfilled order(s) (scoped to this strategy). Reason: {reason}");
                
                foreach (var order in unfilledOrders)
                {
                    try
                    {
                        order.Cancel();
                        AppLog.System("RowanStrategy", "CancelAll", 
                            $"Cancelled: {order.OrderType?.Behavior} {order.Side} @ {order.Price} (Id: {order.Id}, Comment: {order.Comment})");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("RowanStrategy", "CancelAll", 
                            $"Failed to cancel order {order.Id}: {ex.Message}");
                    }
                }
            }
            else
            {
                AppLog.System("RowanStrategy", "CancelAll", $"No orders found to cancel (reason: {reason})");
            }
        }
        
        // End of RowanStrategy class - all methods complete
    }
}
