using Resto.Front.Api.Data.Brd;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.HorecaControlPlugin.Dto;
using Resto.Front.Api.HorecaControlPlugin.Dto.Buttons;
using Resto.Front.Api.HorecaControlPlugin.Dto.Buttons.Interfaces;
using SocketIOClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Resto.Front.Api.HorecaControlPlugin
{
    public sealed partial class ServerEventHandler
    {
        #region ForceRequest

        private Task ServerToPluginFullForceCallback(IEventContext arg)
        {
            PluginContext.Log.Info($"PluginToServerFull (forced) :: started.");
            try
            {
                FullReportResponse();
            }
            catch (Exception e)
            {
                PluginContext.Log.Error($"PluginToServerFull (forced) :: {e.Message}.", e);
            }

            PluginContext.Log.Info($"PluginToServerFull (forced) :: finished.");
            return Task.CompletedTask;
        }

        #endregion

        #region Timer

        private readonly object _activeTasksLock = new();
        private CancellationTokenSource _cts = new();
        private Timer _timer;
        private double defaultTimeout = 2;

        private void StartTimer()
        {
            if (_timer is null)
            {
                _cts = new CancellationTokenSource();
                _timer = new Timer(PluginToServerFull, _cts.Token, TimeSpan.FromSeconds(20),
                    TimeSpan.FromMinutes(defaultTimeout));
                PluginContext.Log.Debug("StartTimer :: started");
            }
        }


        private void StopTimer()
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _cts?.Cancel();

            _timer?.Dispose();
            _timer = null;
            PluginContext.Log.Debug("StopTimer :: finished");
        }

        private void PluginToServerFull(object state)
        {
            var ct = ((CancellationToken)state);
            PluginContext.Log.Info($"PluginToServerFull (by timer) :: started.");
            FullReportResponse();
            PluginContext.Log.Info($"PluginToServerFull (by timer) :: finished.");
        }

        #endregion


        private void FullReportResponse()
        {
            GC.KeepAlive(_timer);
            if (!Monitor.TryEnter(_activeTasksLock)) return;

            PluginContext.Log.Debug($"FullReportResponse :: started.");
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                var activeEmployee =
                    PluginContext.Operations.GetUsers()?.Where(u => u.IsSessionOpen)?
                        .ToList()?.Count ?? 0;
                var orders = PluginContext.Operations.GetOrders()?.Where(x => x.Status != OrderStatus.Deleted)?
                    .ToList() ?? new List<IOrder>();
                var reserves = PluginContext.Operations.GetReserves()?
                    .Where(x => x.Tables.Any() && x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup.Id ==
                        PluginHelpers.GroupName.Id)?
                    .ToList() ?? new List<IReserve>();

                PluginToServerSummaryOfRestaurant pluginToServerSummaryOfRestaurant = null;
                PluginToServerRevenueByWaiters pluginToServerRevenueByWaiters = null;
                PluginToServerStopListRemainingMeals pluginToServerStopListRemainingMeals = null;
                PluginToServerHighRiskOperation pluginToServerHighRiskOperation = null;
                var ordersDetail = new List<PluginToServerOrderDetails>();

                PluginToServerTopTenMealsByRevenue pluginToServerTopTenMealsByRevenue = null;

                try
                {
                    pluginToServerSummaryOfRestaurant =
                        (PluginToServerSummaryOfRestaurant)GenerateRevenueByRestaurantsRegistersFloorsReport(orders,
                            activeEmployee);
                    pluginToServerSummaryOfRestaurant.ShouldSerializeHeader = false;
                }
                catch (Exception ex)
                {
                    PluginContext.Log.Error($"FullReportResponse :: summaryOfRestaurant :: {ex.Message}", ex);
                }

                try
                {
                    pluginToServerRevenueByWaiters =
                        (PluginToServerRevenueByWaiters)GenerateWaitersReport(orders);
                    pluginToServerRevenueByWaiters.ShouldSerializeHeader = false;
                }
                catch (Exception ex)
                {
                    PluginContext.Log.Error($"FullReportResponse :: revenueByWaiters :: {ex.Message}", ex);
                }

                try
                {
#if V8P5
                pluginToServerStopListRemainingMeals =
 (PluginToServerStopListRemainingMeals)GenerateStopListRemainingMeals(PluginContext.Operations.GetProductsRemainingAmounts());
#else
                    pluginToServerStopListRemainingMeals =
                        (PluginToServerStopListRemainingMeals)GenerateStopListRemainingMeals(PluginContext
                            .Operations
                            .GetStopListProductsRemainingAmounts()
                            .ToDictionary(x => x.Key.Product, x => x.Value)
                        );
#endif
                    if (pluginToServerStopListRemainingMeals != null)
                        pluginToServerStopListRemainingMeals.ShouldSerializeHeader = false;
                }
                catch (Exception ex)
                {
                    PluginContext.Log.Error($"FullReportResponse :: stopListRemainingMeals :: {ex.Message}", ex);
                }

                try
                {
                    pluginToServerTopTenMealsByRevenue =
                        (PluginToServerTopTenMealsByRevenue)GenerateTopTenMealsByRevenue(orders);
                    if (pluginToServerTopTenMealsByRevenue != null)
                        pluginToServerTopTenMealsByRevenue.ShouldSerializeHeader = false;
                }
                catch (Exception ex)
                {
                    PluginContext.Log.Error($"FullReportResponse :: topTenMealsByRevenue :: {ex.Message}", ex);
                }

                try
                {
                    pluginToServerHighRiskOperation =
                        (PluginToServerHighRiskOperation)GenerateHighRiskOperations();

                    if (pluginToServerHighRiskOperation != null)
                        pluginToServerHighRiskOperation.ShouldSerializeHeader = false;
                }
                catch (Exception ex)
                {
                    PluginContext.Log.Error($"FullReportResponse :: highRiskOperation :: {ex.Message}", ex);
                }

                PluginContext.Log.Info($"FullReportResponse (orders):: started.");
                foreach (var o in orders)
                {
                    try
                    {
                        var orderData =
                            (PluginToServerOrderDetails)GenerateOrder(orders, $"{o.Number}", reserves);
                        if (orderData != null)
                            orderData.ShouldSerializeHeader = false;

                        ordersDetail.Add(orderData);
                    }
                    catch (Exception ex)
                    {
                        PluginContext.Log.Error($"FullReportResponse :: order {o.Number} :: {ex.Message}", ex);
                    }
                }

                PluginContext.Log.Info($"FullReportResponse (orders):: finished.");

                if (_context == null)
                {
                    PluginContext.Log.Error("FullReportResponse :: _context is null.");
                    return;
                }

                if (_context.Shift == null)
                {
                    PluginContext.Log.Error("FullReportResponse :: _context.Shift is null.");
                    return;
                }

                // Как в hc_250305: при открытой смене closed = null, не 0001-01-01.
                DateTime? closed = _context?.Shift?.CloseTime;
                if (closed == default(DateTime))
                    closed = null;

                IPluginToServer data = new PluginToServerFull
                {
                    Opened = _context?.Shift?.OpenTime,
                    Closed = closed,
                    SummaryOfRestaurant = pluginToServerSummaryOfRestaurant,
                    RevenueByWaiters = pluginToServerRevenueByWaiters,
                    StopListRemainingMeals = pluginToServerStopListRemainingMeals,
                    TopTenMealsByRevenue = pluginToServerTopTenMealsByRevenue,
                    HighRiskOperation = pluginToServerHighRiskOperation,
                    OrdersDetails = ordersDetail,
                };

                var evt = new PluginFullData
                {
                    RequestId = Guid.NewGuid(),
                    Data = data,
                };

                PluginContext.Log.Info($"FullReportResponse :: PluginFullData created.");

                //if ( _client.Connected)
                if (_client != null && _client.Connected)
                {
                    PluginContext.Log.Debug(evt, PluginHelpers.IsDeveloperMode);
                    PluginContext.Log.Debug($"FullReportResponse :: Unpacked length : {evt?.ToJson()?.Length ?? 0}");
                    PluginContext.Log.Debug($"FullReportResponse :: Packed length : {evt?.ToGZip()?.Length ?? 0}");

                    var eventToSend = evt?.ToGZip();

                    if (eventToSend != null)
                    {
                        var task = _client.EmitAsync("plugin_to_server_full", new object[] { eventToSend }, ack =>
                        {
                            PluginContext.Log.Debug(
                                $"FullReportResponse :: response {(ack != null ? ack.RawText : "null")}");
                            return Task.CompletedTask;
                        });

                        try
                        {
                            task.ConfigureAwait(false).GetAwaiter().GetResult(); // Синхронное ожидание завершения
                            PluginContext.Log.Info($"FullReportResponse :: Request completed.");
                        }
                        catch (Exception ex)
                        {
                            PluginContext.Log.Error($"FullReportResponse :: Failed to send request. {ex.Message}", ex);
                            throw; // Пробрасываем ошибку, если требуется
                        }
                    }
                    else
                    {
                        PluginContext.Log.Warn($"FullReportResponse :: eventToSend is null.");
                    }

                }
                else
                {
                    PluginContext.Log.Warn("FullReportResponse :: Socket client is not connected or not initialized.");
                }


                PluginContext.Log.Debug($"FullReportResponse :: finished.");
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"FullReportResponse :: {ex.Message}", ex);
            }
            finally
            {
                lock (_activeTasksLock)
                {
                    Monitor.Exit(_activeTasksLock);
                    _timer?.Change(
                        TimeSpan.FromMinutes(defaultTimeout),
                        TimeSpan.FromMinutes(defaultTimeout));
                    PluginContext.Log.Debug($"FullReportResponse :: completed.");
                }
            }
        }
    }
}