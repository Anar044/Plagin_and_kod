using Resto.Front.Api.Data.Assortment;
using Resto.Front.Api.Data.Brd;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.Payments;
using Resto.Front.Api.HorecaControlPlugin.Core.Application.Services.Interfaces;
using Resto.Front.Api.HorecaControlPlugin.Core.Infrastructure.Communication.Interfaces;
using Resto.Front.Api.HorecaControlPlugin.Core.Infrastructure.Persistence.Repositories.Interfaces;
using Resto.Front.Api.HorecaControlPlugin.Dto;
using Resto.Front.Api.HorecaControlPlugin.Dto.Buttons;
using Resto.Front.Api.HorecaControlPlugin.Dto.Buttons.Interfaces;
using Resto.Front.Api.HorecaControlPlugin.Dto.Events;
using Resto.Front.Api.HorecaControlPlugin.Sqlite;
using Resto.Front.Api.HorecaControlPlugin.Sqlite.Schema;
using SocketIOClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Resto.Front.Api.HorecaControlPlugin;

/// <summary>
/// Обработчик бизнес-событий от сервера (запросы, отчеты)
/// Управление подключением выполняется через SocketConnectionManager
/// </summary>
public sealed partial class ServerEventHandler : IDisposable
{
    private readonly ISocketConnectionManager _connectionManager;
    private readonly IReportGenerator _reportGenerator;
    private readonly IEventPublisher _eventPublisher;
    private readonly IRepository _repository;
    private readonly SocketIOClient.SocketIO _client;
    private DebugSettings _ds;

    /*
                         Url = "",
                Password = "plugin",
                Login = "plugin",
                PluginId = "pluginId",
                PluginName = "pluginName",
                GroupId = "groupId",
                GroupName = "groupName",
                DepartmentId = "b91627c7-3ce4-8029-018e-ebf86baf0010",
                DepartmentName = "departmentName"
         */
    public ServerEventHandler(SocketIoConnectorConfig config, DebugSettings ds,
        SocketIOClient.SocketIO client,
        ISocketConnectionManager connectionManager = null,
        IReportGenerator reportGenerator = null,
        IEventPublisher eventPublisher = null,
        IRepository repository = null)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));

        _client = client;
        _ds = ds;
        _connectionManager = connectionManager;
        _reportGenerator = reportGenerator;
        _eventPublisher = eventPublisher;
        _repository = repository;

        // Логируем оригинальные значения для отладки
        PluginContext.Log.Debug($"ServerEventHandler :: pluginName: '{config.PluginName ?? string.Empty}'");
        PluginContext.Log.Debug($"ServerEventHandler :: groupName: '{config.GroupName ?? string.Empty}'");
        PluginContext.Log.Debug($"ServerEventHandler :: departmentName: '{config.DepartmentName ?? string.Empty}'");
        PluginContext.Log.Debug($"ServerEventHandler :: version: '{config.Version ?? string.Empty}'");
        PluginContext.Log.Debug($"ServerEventHandler :: currencyCode: '{config.CurrencyCode ?? string.Empty}'");

        // Подписываемся на события клиента для бизнес-логики
        // События подключения/отключения обрабатываются в SocketConnectionManager
        _client.On("server_to_plugin", ServerToPluginCallback);
        _client.On("server_to_plugin_full_force", ServerToPluginFullForceCallback);

        // Подписываемся на события SocketConnectionManager для синхронизации
        if (_connectionManager != null)
        {
            _connectionManager.Connected += OnConnectionManagerConnected;
            _connectionManager.Reconnected += OnConnectionManagerReconnected;

        }

        PluginContext.Log.Info($"ServerEventHandler :: started");
    }

    private void OnConnectionManagerConnected(object sender, EventArgs e)
    {
        PluginContext.Log.Info("ServerEventHandler :: ConnectionManager connected, initializing...");
        InitializeAfterConnection();
    }

    private void OnConnectionManagerReconnected(object sender, int attempt)
    {
        PluginContext.Log.Info($"ServerEventHandler :: ConnectionManager reconnected (attempt {attempt}), initializing...");
        InitializeAfterConnection();
    }

    private void InitializeAfterConnection()
    {
        CashServerStart();
        SendUnsendEvents();
        StartTimer();
    }

    #region Диагностика и логирование
    // Метод LogWebSocketUrlFromError перенесен в SocketConnectionManager
    #endregion

    #region Отправка не отправленных событий/сообщений
    // Методы удалены - используйте _connectionManager.SendUnsentEvents() и SendUnsentMessages() напрямую
    private void SendUnsendMessages() => _connectionManager?.SendUnsentMessages();
    private void SendUnsendEvents() => _connectionManager?.SendUnsentEvents();
    #endregion


    private void CashServerStart()
    {
        var startEvent = new PluginToServerEvent
        {
            PluginEventType = EnumPluginEventType.CashRegisterStart,
            Data = new PluginToServerEventStartStopCashServer(),
        };
        
        if (_eventPublisher != null)
        {
            _eventPublisher.PublishEvent(startEvent);
        }
        else if (_connectionManager != null)
        {
            _connectionManager.SendEvent(startEvent);
        }
        else
        {
            PluginContext.Log.Error("CashServerStart :: Neither EventPublisher nor ConnectionManager is available.");
            _repository?.AddEvent(startEvent);
        }
    }

    private void CashServerStop()
    {
        try
        {
            var stopEvent = new PluginToServerEvent
            {
                PluginEventType = EnumPluginEventType.CashRegisterShutDown,
                Data = new PluginToServerEventStartStopCashServer(),
            };
            
            if (_eventPublisher != null)
            {
                _eventPublisher.PublishEvent(stopEvent);
            }
            else if (_connectionManager != null)
            {
                _connectionManager.SendEvent(stopEvent);
            }
            else if (_repository != null)
            {
                PluginContext.Log.Warn("CashServerStop :: Neither EventPublisher nor ConnectionManager is available, saving to repository.");
                _repository.AddEvent(stopEvent);
            }
            else
            {
                PluginContext.Log.Warn("CashServerStop :: No available services to send stop event.");
            }
        }
        catch (ObjectDisposedException ex)
        {
            PluginContext.Log.Warn($"CashServerStop :: Cannot send stop event, resources are already disposed: {ex.Message}");
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"CashServerStop :: Error sending stop event: {ex.Message}", ex);
        }
    }




    private Task ServerToPluginCallback(IEventContext arg)
    {
        try
        {
            PluginContext.Log.Info($"ServerToPluginCallback :: Received.");
            PluginContext.Log.Debug($"ServerToPluginCallback :: Received {arg.RawText}");
            var reqData = arg.GetValue<PluginEventData>(0);
            if (reqData != null)
            {
                PluginContext.Log.Info($"ServerToPluginCallback :: Request parser {reqData.RequestId} started.");
                var response = new PluginEventData
                {
                    ChatId = reqData.ChatId,
                    RequestId = reqData.RequestId,
                    RequestType = reqData.RequestType,
                    //  RequestDetail = reqData?.RequestDetail ?? null,
                };
                IPluginToServer data = null;

                PluginContext.Log.Debug($"ServerToPluginCallback :: get local data start.");
                var activeEmployee =
                    PluginContext.Operations.GetUsers()?.Count(u => u.IsSessionOpen) ?? 0;
                var orders = PluginContext.Operations.GetOrders()?.Where(x => x.Status != OrderStatus.Deleted)?
                    .ToList() ?? new List<IOrder>();

                var reserves = PluginContext.Operations.GetReserves()?
                    .Where(x => x.Tables.Any() && x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup.Id ==
                        PluginHelpers.GroupName.Id)?
                    .ToList() ?? new List<IReserve>();

                PluginContext.Log.Debug($"ServerToPluginCallback :: get local data finish.");

                // Используем IReportGenerator, если он передан, иначе используем старые методы (для обратной совместимости)
                if (_reportGenerator != null)
                {
                    switch (reqData.RequestType)
                    {
                        case EnumRequestType.SummaryOfRestaurant:
                            data = _reportGenerator.GenerateSummaryReport(orders, activeEmployee);
                            break;
                        case EnumRequestType.RevenueByRestaurantsRegistersFloors:
                            data = _reportGenerator.GenerateRevenueByFloorsReport(orders, activeEmployee);
                            break;
                        case EnumRequestType.RevenueByWaiters:
                            data = _reportGenerator.GenerateWaitersReport(orders);
                            break;
                        case EnumRequestType.CurrentShiftOrdersList:
                            data = _reportGenerator.GenerateCurrentShiftOrders(orders, reserves);
                            break;
                        case EnumRequestType.TopTenMealsByRevenue:
                            data = _reportGenerator.GenerateTopTenMealsReport(orders);
                            break;
                        case EnumRequestType.StopListRemainingMeals:
#if V8P5
                            data = _reportGenerator.GenerateStopListReport(PluginContext.Operations.GetProductsRemainingAmounts());
#else
                            data = _reportGenerator.GenerateStopListReport(PluginContext.Operations
                                .GetStopListProductsRemainingAmounts().ToDictionary(x => x.Key.Product, x => x.Value)
                            );
#endif
                            break;
                        case EnumRequestType.TablesWithOpenOrders:
                            data = _reportGenerator.GenerateTablesWithOpenOrders(orders);
                            break;
                        case EnumRequestType.HighRiskOperations:
                            data = _reportGenerator.GenerateHighRiskOperationsReport();
                            break;
                        case EnumRequestType.Order:
                            var orderNumber = int.TryParse(reqData.RequestDetail, out var num) ? num : 0;
                            data = _reportGenerator.GenerateOrderDetails(orderNumber, orders, reserves);
                            break;
                        default:
                            throw new Exception("No RequestType in request.");
                    }
                }
                else
                {
                    // Обратная совместимость: используем старые методы
                    switch (reqData.RequestType)
                    {
                        case EnumRequestType.SummaryOfRestaurant:
                            data = GenerateSummaryDetailReport(orders, activeEmployee);
                            break;
                        case EnumRequestType.RevenueByRestaurantsRegistersFloors:
                            data = GenerateRevenueByRestaurantsRegistersFloorsReport(orders, activeEmployee);
                            break;
                        case EnumRequestType.RevenueByWaiters:
                            data = GenerateWaitersReport(orders);
                            break;
                        case EnumRequestType.CurrentShiftOrdersList:
                            data = GenerateCurrentShiftOrders(orders, reserves);
                            break;
                        case EnumRequestType.TopTenMealsByRevenue:
                            data = GenerateTopTenMealsByRevenue(orders);
                            break;
                        case EnumRequestType.StopListRemainingMeals:
#if V8P5
                            data = GenerateStopListRemainingMeals(PluginContext.Operations.GetProductsRemainingAmounts());
#else
                            data = GenerateStopListRemainingMeals(PluginContext.Operations
                                .GetStopListProductsRemainingAmounts().ToDictionary(x => x.Key.Product, x => x.Value)
                            );
#endif
                            break;
                        case EnumRequestType.TablesWithOpenOrders:
                            data = GenerateTablesWithOpenOrders(orders);
                            break;
                        case EnumRequestType.HighRiskOperations:
                            data = GenerateHighRiskOperations();
                            break;
                        case EnumRequestType.Order:
                            data = GenerateOrder(orders, reqData.RequestDetail, reserves);
                            break;
                        default:
                            throw new Exception("No RequestType in request.");
                    }
                }

                if (data is null)
                    throw new Exception("No data to response.");
                response.Data = data;
                PluginContext.Log.Debug($"ServerToPluginCallback ::\n{response.ToJson()}");
                
                // Используем ConnectionManager для отправки ответа
                if (_connectionManager != null)
                {
                    if (!_connectionManager.SendMessage(response))
                    {
                        PluginContext.Log.Warn("ServerToPluginCallback :: Message was queued, not sent immediately.");
                    }
                }
                else
                {
                    PluginContext.Log.Error("ServerToPluginCallback :: ConnectionManager is null, cannot send response.");
                    // Сохраняем в контекст как fallback
                    _context?.AddMessage(response);
                }
                PluginContext.Log.Info($"ServerToPluginCallback :: Request parser {reqData.RequestId} finished.");
            }
            else
            {
                throw new Exception("Request Empty.");
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"ServerToPluginCallback :: {ex.Message}");
        }

        return Task.CompletedTask;
    }


    public async void Dispose()
    {
        try
        {
            CashServerStop();
            // Клиент управляется через SocketConnectionManager и будет освобожден там
            // Здесь мы только отписываемся от событий
            if (_connectionManager != null)
            {
                _connectionManager.Connected -= OnConnectionManagerConnected;
                _connectionManager.Reconnected -= OnConnectionManagerReconnected;
            }
            PluginContext.Log.Info("ServerEventHandler :: Disposed.");
        }
        catch (Exception e)
        {
            PluginContext.Log.Error($"Dispose error: {e.Message}", e);
        }
    }

    #region Reports

    /// <summary>
    /// Отчет по открытым столам
    /// </summary>
    /// <param name="orders"></param>
    /// <returns></returns>
    private IPluginToServer GenerateTablesWithOpenOrders(List<IOrder> orders)
    {
        PluginToServerTablesWithOpenOrders report = null;
        try
        {
            report = new PluginToServerTablesWithOpenOrders
            {
                TerminalsGroup = PluginHelpers.GroupName.Name,
                RestaurantSections = new List<PluginToServerTablesWithOpenOrdersRestaurantSections>()
            };

            var rsOrder = orders.Where(x =>
                x.Status is OrderStatus.New or OrderStatus.Bill
                && x.Tables.Any()
                && x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup.Id == PluginHelpers.GroupName.Id
            ).ToList();

            foreach (var rsItem in rsOrder.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection))
            {
                var rs = report.RestaurantSections.FirstOrDefault(x => x.RestaurantSectionId == rsItem.Key.Id);
                if (rs is null)
                {
                    rs = new PluginToServerTablesWithOpenOrdersRestaurantSections
                    {
                        RestaurantSectionId = rsItem.Key.Id,
                        RestaurantSectionName = rsItem.Key.Name,
                        Tables = new List<PluginToServerTablesWithOpenOrdersRestaurantSectionsTable>()
                    };
                    report.RestaurantSections.Add(rs);
                }

                foreach (var order in rsItem)
                {
                    foreach (var table in order.Tables)
                    {
                        var tabl = rs.Tables.FirstOrDefault(x => x.TableNumber == table.Number);
                        if (tabl is null)
                        {
                            tabl = new PluginToServerTablesWithOpenOrdersRestaurantSectionsTable
                            {
                                TableNumber = table.Number,
                                OrderNums = new List<int>(),
                            };
                            rs.Tables.Add(tabl);
                        }

                        tabl.OrderNums.Add(order.Number);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"GenerateTablesWithOpenOrders :: {ex.Message}", ex);
        }

        return report;
    }


    /// <summary>
    /// ОТЧЕТ ПО ВЫРУЧКЕ и ДЕТАЛИЗИРОВАННЫЙ ОТЧЕТ ПО ВЫРУЧКЕ
    /// </summary>
    /// <param name="orders"></param>
    /// <param name="activeEmployee"></param>
    /// <returns></returns>
    private IPluginToServer GenerateSummaryDetailReport(List<IOrder> orders, int activeEmployee)
    {
        PluginContext.Log.Debug($"GenerateSummaryDetailReport :: started.");

        var data = new PluginToServerSummaryOfRestaurant
        {
            ActiveEmployees = activeEmployee
        };


        // var data = new PluginToServerSummaryOfRestaurant
        // {
        //     ActiveEmployees = activeEmployee,
        // };

        try
        {
            var groupedOrders = orders
                .Where(o => o.Tables.Any() && o.Status != OrderStatus.Deleted)
                .GroupBy(o => o.Tables.First().RestaurantSection?.TerminalsGroup)
                .ToDictionary(
                    tg => tg.Key?.Id ?? Guid.Empty,
                    tg => tg.GroupBy(o => o.Tables
                        .First().RestaurantSection)
                        .ToDictionary(rs => rs.Key?.Id ?? Guid.Empty, rs => rs.ToList())
                );



            foreach (var terminalGroup in data.TerminalsGroups)
            {
                var terminalGroupId = terminalGroup.Id;
                if (!groupedOrders.TryGetValue(terminalGroupId, out var restaurantSections))
                    continue;

                PluginContext.Log.Debug($"Processing TerminalGroup: {terminalGroup.Name}");

                foreach (var restaurantSection in terminalGroup.RestaurantSections)
                {
                    var sectionId = restaurantSection.Id;
                    if (!restaurantSections.TryGetValue(sectionId, out var sectionOrders))
                        continue;

                    PluginContext.Log.Debug($"Processing RestaurantSection: {restaurantSection.Name}");

                    foreach (var order in sectionOrders)
                    {
                        UpdateRestaurantSectionMetrics(restaurantSection, order);
                    }
                }

                terminalGroup.CalculateTotalGroup();
                PluginContext.Log.Info($"TerminalGroup processed: {terminalGroup.ToJson()}");
            }

            data.CalculateTotal();
            PluginContext.Log.Debug("GenerateSummaryDetailReport completed.");




            // foreach (var tgGroup in orders.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup))
            // {
            //     var orderTerminalGroup = data.TerminalsGroups.FirstOrDefault(x => x.Id == tgGroup.Key.Id);
            //     if (orderTerminalGroup is null)
            //         continue;
            //     PluginContext.Log.Debug($"GenerateSummaryDetailReport :: tgGroup {tgGroup.Key.Name} started");
            //     foreach (var rsGroup in tgGroup.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection))
            //     {
            //         var orderRestuarantSections =
            //             orderTerminalGroup.RestaurantSections.FirstOrDefault(x => x.Id == rsGroup.Key.Id);
            //         if (orderRestuarantSections is null)
            //             continue;
            //         PluginContext.Log.Debug($"GenerateSummaryDetailReport :: rsGroup {rsGroup.Key.Name} started");
            //         foreach (var order in rsGroup)
            //         {
            //             switch (order.Status)
            //             {
            //                 case OrderStatus.New:
            //                 case OrderStatus.Bill:
            //                     orderRestuarantSections.OpenedOrders++;
            //                     orderRestuarantSections.OpenOrdersMoneySum += order.ResultSum;
            //                     orderRestuarantSections.ExpectedRevenueMoneySum += order.ResultSum;
            //                     orderRestuarantSections.ActiveTables += order.Tables.Count;
            //                     orderRestuarantSections.NumberOfGuest += order.Guests?.Count ?? 0;
            //                     break;
            //                 case OrderStatus.Closed:
            //                     orderRestuarantSections.ClosedOrders++;
            //                     orderRestuarantSections.ClosedOrdersMoneySum += order.ResultSum;
            //                     orderRestuarantSections.ExpectedRevenueMoneySum += order.ResultSum;
            //                     orderRestuarantSections.NumberOfGuest += order.Guests?.Count ?? 0;
            //                     break;
            //             }
            //
            //             orderRestuarantSections.OrderCount++;
            //             orderRestuarantSections.CalculationAllDiscountsDonationsPayments(order);
            //         }
            //
            //         PluginContext.Log.Debug($"GenerateSummaryDetailReport :: rsGroup {rsGroup.Key.Name} started");
            //     }
            //
            //     PluginContext.Log.Info(orderTerminalGroup.ToJson());
            //     PluginContext.Log.Debug($"GenerateSummaryDetailReport :: tgGroup {tgGroup.Key.Name} finished");
            // }
            //
            // data.TerminalsGroups.ForEach(x => x.CalculateTotalGroup());
            //
            // data.CalculateTotal();
            // PluginContext.Log.Debug($"GenerateSummaryDetailReport :: finished.");
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GenerateSummaryDetailReport :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GenerateSummaryDetailReport :: {ex.Message}");
        }

        return data;
    }


    private void UpdateRestaurantSectionMetrics(PluginToServerSummaryOfRestaurantTerminalsGroupRestaurantSection section, IOrder order)
    {
        section.OrderCount++;

        switch (order.Status)
        {
            case OrderStatus.New:
            case OrderStatus.Bill:
                section.OpenedOrders++;
                section.OpenOrdersMoneySum += order.ResultSum;
                section.ExpectedRevenueMoneySum += order.ResultSum;
                section.ActiveTables += order.Tables.Count;
                section.NumberOfGuest += order.Guests?.Count ?? 0;
                break;
            case OrderStatus.Closed:
                section.ClosedOrders++;
                section.ClosedOrdersMoneySum += order.ResultSum;
                section.ExpectedRevenueMoneySum += order.ResultSum;
                section.NumberOfGuest += order.Guests?.Count ?? 0;
                break;
        }

        section.CalculationAllDiscountsDonationsPayments(order);
    }




    private IPluginToServer GenerateRevenueByRestaurantsRegistersFloorsReport(List<IOrder> orders, int activeEmployee)
    {
        PluginContext.Log.Debug($"GenerateRevenueByRestaurantsRegistersFloorsReport :: started.");
        var data = new PluginToServerSummaryOfRestaurant
        {
            ActiveEmployees = activeEmployee,
        };

        try
        {
            foreach (var tgGroup in orders.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup))
            {
                var orderTerminalGroup = data.TerminalsGroups.FirstOrDefault(x => x.Id == tgGroup.Key.Id);
                if (orderTerminalGroup is null)
                    continue;
                PluginContext.Log.Debug(
                    $"GenerateRevenueByRestaurantsRegistersFloorsReport :: tgGroup {tgGroup.Key.Name} started");
                foreach (var rsGroup in tgGroup.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection))
                {
                    var orderRestuarantSections =
                        orderTerminalGroup.RestaurantSections.FirstOrDefault(x => x.Id == rsGroup.Key.Id);
                    if (orderRestuarantSections is null)
                        continue;
                    PluginContext.Log.Debug(
                        $"GenerateRevenueByRestaurantsRegistersFloorsReport :: rsGroup {rsGroup.Key.Name} started");
                    foreach (var order in rsGroup)
                    {
                        switch (order.Status)
                        {
                            case OrderStatus.New:
                            case OrderStatus.Bill:
                                if (order.IsBanquetOrder)
                                {
                                    orderRestuarantSections.OpenedBanquetOrders++;
                                    orderRestuarantSections.OpenBanquetOrdersMoneySum += order.ResultSum;
                                    orderRestuarantSections.ExpectedBanquetRevenueMoneySum += order.ResultSum;
                                    orderRestuarantSections.ActiveBanquetTables += order.Tables.Count;
                                    orderRestuarantSections.BanquetNumberOfGuest += order.Guests?.Count ?? 0;
                                }
                                else
                                {
                                    orderRestuarantSections.OpenedOrders++;
                                    orderRestuarantSections.OpenOrdersMoneySum += order.ResultSum;
                                    orderRestuarantSections.ExpectedRevenueMoneySum += order.ResultSum;
                                    orderRestuarantSections.ActiveTables += order.Tables.Count;
                                    orderRestuarantSections.NumberOfGuest += order.Guests?.Count ?? 0;
                                }

                                break;
                            case OrderStatus.Closed:
                                if (order.IsBanquetOrder)
                                {
                                    orderRestuarantSections.ClosedBanquetOrders++;
                                    orderRestuarantSections.ClosedBanquetOrdersMoneySum += order.ResultSum;
                                    orderRestuarantSections.ExpectedBanquetRevenueMoneySum += order.ResultSum;
                                    orderRestuarantSections.BanquetNumberOfGuest += order.Guests?.Count ?? 0;
                                }
                                else
                                {
                                    orderRestuarantSections.ClosedOrders++;
                                    orderRestuarantSections.ClosedOrdersMoneySum += order.ResultSum;
                                    orderRestuarantSections.ExpectedRevenueMoneySum += order.ResultSum;
                                    orderRestuarantSections.NumberOfGuest += order.Guests?.Count ?? 0;
                                }

                                break;
                        }

                        orderRestuarantSections.OrderCount++;
                        orderRestuarantSections.CalculationAllDiscountsDonationsPayments(order);
                    }

                    PluginContext.Log.Debug(
                        $"GenerateRevenueByRestaurantsRegistersFloorsReport :: rsGroup {rsGroup.Key.Name} finished");
                }

                PluginContext.Log.Info(orderTerminalGroup.ToJson());
                PluginContext.Log.Debug(
                    $"GenerateRevenueByRestaurantsRegistersFloorsReport :: tgGroup {tgGroup.Key.Name} finished.");
            }

            data.TerminalsGroups.ForEach(x => x.CalculateFloorTotalGroup());
            data.CalculateTotal();
            PluginContext.Log.Debug($"GenerateRevenueByRestaurantsRegistersFloorsReport :: finished.");
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GenerateRevenueByRestaurantsRegistersFloorsReport :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GenerateRevenueByRestaurantsRegistersFloorsReport :: {ex.Message}");
        }

        return data;
    }


    /// <summary>
    /// ОТЧЕТ ПО СОТРУДНИКАМ
    /// </summary>
    /// <param name="orders"></param>
    /// <returns></returns>
    private IPluginToServer GenerateWaitersReport(List<IOrder> orders)
    {
        PluginContext.Log.Debug($"GenerateWaitersReport :: started.");
        var waiterData = new PluginToServerRevenueByWaiters
        {
            DepartmentName = PluginHelpers.DepartmentName?.Name ?? string.Empty,
        };
        try
        {
            //waiterOrders.Key
            foreach (var orderTg in orders.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup))
            {
                var tg = waiterData.TerminalGroups.FirstOrDefault(x =>
                    x.TerminalsGroupId == orderTg.Key.Id);
                if (tg is null)
                {
                    tg = new PluginToServerRevenueByWaitersTerminalGroup
                    {
                        TerminalsGroupId = orderTg.Key.Id,
                        TerminalsGroupName = orderTg.Key.Name,
                    };
                    waiterData.TerminalGroups.Add(tg);
                }

                foreach (var waiterOrders in orders.Where(o => o.Waiter != null).GroupBy(x => x.Waiter))
                {
                    var waiterOrdersInTg =
                        tg.Waiters.FirstOrDefault(x => x.WaiterId == waiterOrders.Key.Id);
                    if (waiterOrdersInTg is null)
                    {
                        waiterOrdersInTg = new PluginToServerRevenueByWaitersTerminalGroupWaiter
                        {
                            WaiterId = waiterOrders.Key.Id,
                            WaiterName = waiterOrders.Key.Name,
                            NumberOfGuest = waiterOrders?.Sum(x => x.Guests?.Count ?? 0) ?? 0,
                            HighRiskOperations = _context?.GetHighRiskOperations(waiterOrders.Key) ?? 0,
                            OpenedOrders = 0,
                            ClosedOrders = 0,
                            OpenOrdersMoneySum = 0,
                            ClosedOrdersMoneySum = 0
                        };
                        tg.Waiters.Add(waiterOrdersInTg);
                    }

                    foreach (var order in waiterOrders)
                    {
                        waiterOrdersInTg.NumberOfGuest += order.Guests?.Count ?? 0;
                        switch (order.Status)
                        {
                            case OrderStatus.New:
                            case OrderStatus.Bill:
                                waiterOrdersInTg.OpenedOrders++;
                                waiterOrdersInTg.OpenOrdersMoneySum += order.ResultSum;
                                break;
                            case OrderStatus.Closed:
                                waiterOrdersInTg.ClosedOrders++;
                                waiterOrdersInTg.ClosedOrdersMoneySum += order.ResultSum;
                                break;
                        }
                    }
                }

                tg.Waiters = tg.Waiters.OrderBy(x => x.WaiterName).ToList();
            }

            waiterData.TerminalGroups =
                waiterData.TerminalGroups.OrderBy(x => x.TerminalsGroupName).ToList();
            PluginContext.Log.Debug($"GenerateWaitersReport :: finished.");
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GenerateWaitersReport :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GenerateWaitersReport :: {ex.Message}");
        }

        return waiterData;
    }

    /// <summary>
    /// СПИСОК ЗАКАЗОВ СМЕНЫ
    /// </summary>
    /// <param name="orders"></param>
    /// <param name="reserves"></param>
    /// <returns></returns>
    private IPluginToServer GenerateCurrentShiftOrders(List<IOrder> orders, List<IReserve> reserves)
    {
        PluginContext.Log.Debug($"GenerateCurrentShiftOrders :: started.");
        var reportData = new PluginToServerCurrentShiftOrders();
        try
        {
            var reservesWithOrders = reserves.Where(x =>
                    x.Order != null
                    && x.CancelReason == null
                    && x.EstimatedStartTime >= _context.Shift.OpenTime)
                .Select(x => x.Order)?.ToList() ?? new List<IOrder>();

            var deviveryOrders = PluginContext.Operations.GetDeliveryOrders();

            var ordersNonReserves = orders
                .Where(x =>
                    !reservesWithOrders.Contains(x)
                    && !deviveryOrders.Contains(x)
                ).ToList();

            #region Обработка простых заказов

            foreach (var tgGroup in ordersNonReserves.GroupBy(x =>
                         x.Tables.FirstOrDefault()?.RestaurantSection.TerminalsGroup))
            {
                var orderTerminalGroup = reportData.TerminalsGroups.FirstOrDefault(x => x.Id == tgGroup.Key.Id);
                if (orderTerminalGroup is null)
                    continue;
                foreach (var rsGroup in tgGroup.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection))
                {
                    var orderRestuarantSections =
                        orderTerminalGroup.RestaurantSections.FirstOrDefault(x => x.Id == rsGroup.Key.Id);
                    if (orderRestuarantSections is null)
                        continue;
                    foreach (var order in rsGroup)
                    {
                        var status = order.Status switch
                        {
                            OrderStatus.New => EnumOrderStatusDto.New,
                            OrderStatus.Bill => EnumOrderStatusDto.Bill,
                            OrderStatus.Closed => EnumOrderStatusDto.Closed,
                            OrderStatus.Deleted => EnumOrderStatusDto.Deleted,
                            _ => throw new ArgumentOutOfRangeException()
                        };

                        orderRestuarantSections.Orders.Add(new CurrentShiftOrdersDto
                        {
                            OrderNum = order.Number,
                            OrderOpenDate = order.OpenTime,
                            OrderExpectedRevenue = order.ResultSum,
                            OrderStatus = status,
                            OrderTables = order.Tables.GetTablesAsString(),
                            OrderBillTime = order.BillTime,
                            OrderCloseTime = order.CloseTime,
                        });
                    }
                }
            }

            #endregion

            #region Обработка доставочных заказов

            foreach (var tgGroup in deviveryOrders.GroupBy(x =>
                         x.Tables.FirstOrDefault()?.RestaurantSection.TerminalsGroup))
            {
                var orderTerminalGroup = reportData.TerminalsGroups.FirstOrDefault(x => x.Id == tgGroup.Key.Id);
                if (orderTerminalGroup is null)
                    continue;
                foreach (var rsGroup in tgGroup.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection))
                {
                    var orderRestuarantSections =
                        orderTerminalGroup.RestaurantSections.FirstOrDefault(x => x.Id == rsGroup.Key.Id);
                    if (orderRestuarantSections is null)
                        continue;
                    foreach (var order in rsGroup)
                    {
                        var deliveryStatus = order.DeliveryStatus switch
                        {
                            DeliveryStatus.Unconfirmed => EnumDeliveryOrderStatusDto.Unconfirmed,
                            DeliveryStatus.New => EnumDeliveryOrderStatusDto.New,
                            DeliveryStatus.Waiting => EnumDeliveryOrderStatusDto.Waiting,
                            DeliveryStatus.OnWay => EnumDeliveryOrderStatusDto.OnWay,
                            DeliveryStatus.Delivered => EnumDeliveryOrderStatusDto.Delivered,
                            DeliveryStatus.Closed => EnumDeliveryOrderStatusDto.Closed,
                            DeliveryStatus.Cancelled => EnumDeliveryOrderStatusDto.Cancelled,
                            _ => EnumDeliveryOrderStatusDto.Unconfirmed
                        };


                        var address = GenerateDeliveryAddress(order);
                        var deliveryServiceType = order.OrderType?.OrderServiceType.ToString();


                        orderRestuarantSections.Deliveries.Add(new CurrentShiftOrdersDto
                        {
                            OrderNum = order.Number,
                            OrderOpenDate = order.OpenTime,
                            OrderExpectedRevenue = order.ResultSum,
                            DeliveryOrderStatus = deliveryStatus,
                            OrderTables = order.Tables.GetTablesAsString(),
                            DeliveryServiceType = deliveryServiceType,
                            DeliveryAddress = address,
                            DeliveryCancelTime = order.CancelTime,
                            DeliveryConfirmTime = order.ConfirmTime,
                            DeliveryCreateTime = order.CreateTime,
                            DeliveryPrintTime = order.PrintTime,
                            DeliveryOpenTime = order.OpenTime,
                            DeliverySendTime = order.SendTime,
                            DeliveryExpectedDeliverTime = order.ExpectedDeliverTime,
                            DeliveryCookingFinishTime = order.CookingFinishTime,
                            DeliveryDeliveryCloseTime = order.DeliveryCloseTime,
                            DeliveryPredictedCookingCompleteTime = order.PredictedCookingCompleteTime,
                            DeliveryActualDeliverTime = order.ActualDeliverTime,
                            DeliveryPredictedDeliveryTime = order.PredictedDeliveryTime,
                            DeliveryDuration = order.Duration,
                            DeliveryExpectedDuration = order.ExpectedDuration,
                        });
                    }
                }
            }

            #endregion

            #region Обработка резервных/банкетных заказов

            foreach (var tgGroup in
                     reserves?.Where(x => x.CancelReason == null
                                          && x.EstimatedStartTime >= _context.Shift.OpenTime
                     )?.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup))
            {
                var orderTerminalGroup = reportData.TerminalsGroups.FirstOrDefault(x => x.Id == tgGroup.Key.Id);
                if (orderTerminalGroup is null)
                    continue;
                foreach (var rsGroup in tgGroup.GroupBy(x => x.Tables.FirstOrDefault()?.RestaurantSection))
                {
                    var orderRestuarantSections =
                        orderTerminalGroup.RestaurantSections.FirstOrDefault(x => x.Id == rsGroup.Key.Id);
                    if (orderRestuarantSections is null)
                        continue;
                    foreach (var reserve in rsGroup)
                    {
                        var reserveStatus = reserve.Status switch
                        {
                            ReserveStatus.New => EnumReserveStatusDto.New,
                            ReserveStatus.Started => EnumReserveStatusDto.Started,
                            ReserveStatus.Closed => EnumReserveStatusDto.Closed,
                            _ => throw new ArgumentOutOfRangeException()
                        };

                        EnumOrderStatusDto? status = null;

                        if (reserve.Order is not null)
                            status = reserve.Order.Status switch
                            {
                                OrderStatus.New => EnumOrderStatusDto.New,
                                OrderStatus.Bill => EnumOrderStatusDto.Bill,
                                OrderStatus.Closed => EnumOrderStatusDto.Closed,
                                OrderStatus.Deleted => EnumOrderStatusDto.Deleted,
                                _ => null
                            };

                        orderRestuarantSections.Reserves.Add(new CurrentShiftReserveDto
                        {
                            ReserveEstimatedStartTime = reserve.EstimatedStartTime,
                            ReserveStartTime = reserve.GuestsComingTime,
                            ReserveDuration = reserve.Duration,
                            ReserveTables = reserve.Tables.GetTablesAsString(),
                            ReserveTime = reserve.EstimatedStartTime,
                            ReserveStatus = reserveStatus,
                            ReserveOrder = (reserve.Order is null)
                                ? null
                                : new CurrentShiftOrdersDto
                                {
                                    OrderNum = reserve.Order.Number,
                                    OrderOpenDate = reserve.Order.OpenTime,
                                    OrderBillTime = reserve.Order.BillTime,
                                    OrderCloseTime = reserve.Order.CloseTime,
                                    OrderExpectedRevenue = reserve.Order.ResultSum,
                                    OrderStatus = status,
                                    OrderTables = reserve.Order.Tables.GetTablesAsString(),
                                },
                            ReserveClientName = string.Join(" ",
                                new[] { (reserve.Client?.Name ?? ""), (reserve.Client?.Surname ?? "") }),
                            ReserveClientPhone = reserve.Client?.Phones?.FirstOrDefault(x => x.IsMain)?.Value ?? "",
                        });
                    }
                }
            }

            #endregion

            PluginContext.Log.Debug($"GenerateCurrentShiftOrders :: finished.");
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GenerateCurrentShiftOrders :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GenerateCurrentShiftOrders :: {ex.Message}");
        }


        return reportData;
    }

    /// <summary>
    /// Детализация по заказам
    /// </summary>
    /// <param name="orders"></param>
    /// <param name="reqDataRequestDetail"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    private IPluginToServer GenerateOrder(List<IOrder> orders, string reqDataRequestDetail, List<IReserve> reserves)
    {
        PluginToServerOrderDetails result = null;
        try
        {
            // PluginContext.Log.Debug($"GenerateOrder :: started.", !PluginHelpers.IsDeveloperMode);
            var orderNumber = Convert.ToInt32(reqDataRequestDetail);

            var sectionIds = PluginContext.Operations.GetRestaurantSections()?
                .Where(x => x.TerminalsGroup.Id == PluginHelpers.GroupName.Id)?
                .Select(x => x.Id).ToList() ?? new List<Guid>();
            if (!sectionIds.Any())
                throw new Exception("Нет секции в группе");
            var terminalsGroupOrders =
                orders.Where(o => o.Tables.Any())
                    ?.Where(x => sectionIds.Contains(x.Tables.First().RestaurantSection.Id))?.ToList() ??
                new List<IOrder>();
            if (!terminalsGroupOrders.Any())
                throw new Exception("Нет подходящей терминальной группы");


            var order = terminalsGroupOrders.FirstOrDefault(o =>
                o.Number == orderNumber && o.Status != OrderStatus.Deleted);
            if (order is null)
                throw new Exception("Нет подходящего заказа терминальной группы");

            string reserveClientName = null;
            string reserveClientPhone = null;
            DateTime? reserveGuestComingTime = null;
            TimeSpan? reserveDuration = null;
            DateTime? reserveEstimatedStartTime = null;
            var notNullOrderReserves = reserves?.Where(x => x.Order != null)?.ToList() ?? new List<IReserve>();

            if (notNullOrderReserves.Any())
            {
                var reserveData = notNullOrderReserves.FirstOrDefault(x => x.Order.Id == order.Id);
                if (reserveData is not null)
                {
                    reserveClientName = string.Join(" ",
                        new[] { (reserveData.Client?.Name ?? ""), (reserveData.Client?.Surname ?? "") });
                    reserveClientPhone = reserveData.Client?.Phones?.FirstOrDefault(x => x.IsMain)?.Value ?? "";

                    reserveGuestComingTime = reserveData.GuestsComingTime;
                    reserveDuration = reserveData.Duration;
                    reserveEstimatedStartTime = reserveData.EstimatedStartTime;
                }
            }

            List<KeyValueClass> surcharges = null;
            List<KeyValueClass> discounts = null;
            List<KeyValueClass> payments = null;
            List<KeyValueClass> tips = null;
            if (order.AppliedDiscounts.Any())
            {
                order.AppliedDiscounts.ToList().ForEach(d =>
                {
                    if (d.DiscountSum >= 0)
                    {
                        discounts ??= new List<KeyValueClass>();
                        var dsc = discounts.FirstOrDefault(x => x.Id == d.Discount.DiscountType.Id);
                        if (dsc is null)
                        {
                            dsc = new KeyValueClass
                            {
                                Name = d.Discount.DiscountType.Name,
                                Value = 0,
                                Id = d.Discount.DiscountType.Id,
                            };
                            discounts.Add(dsc);
                        }

                        dsc.Value += d.DiscountSum;
                    }
                    else
                    {
                        surcharges ??= new List<KeyValueClass>();
                        var src = surcharges.FirstOrDefault(x => x.Id == d.Discount.DiscountType.Id);
                        if (src is null)
                        {
                            src = new KeyValueClass
                            {
                                Name = d.Discount.DiscountType.Name,
                                Value = 0,
                                Id = d.Discount.DiscountType.Id,
                            };
                            surcharges.Add(src);
                        }

                        src.Value += (-1M * d.DiscountSum);
                    }
                });
            }

            var donations =
                order.Donations?.Where(x => x.Status != PaymentStatus.Cancelled && x.Status != PaymentStatus.Storned)
                    ?.ToList() ?? new List<IPaymentItem>();
            if (donations.Any())
            {
                donations.ForEach(t =>
                {
                    var type = EnumPaymentType.Other;
                    if (t.Type.PrintCheque && t.Type.Kind == PaymentTypeKind.Cash)
                        type = EnumPaymentType.Cash;
                    else if (t.Type.PrintCheque && t.Type.Kind != PaymentTypeKind.Cash)
                        type = EnumPaymentType.Card;


                    tips ??= new List<KeyValueClass>();
                    var tp = tips.FirstOrDefault(x => x.Id == t.Type.Id
                                                      && x.Type == type
                    );
                    if (tp is null)
                    {
                        tp = new KeyValueClass
                        {
                            Name = t.Type.Name,
                            Value = 0,
                            Type = type,
                            Id = t.Type.Id,
                        };
                        tips.Add(tp);
                    }

                    tp.Value += t.Sum;
                });
            }

            var paymentsO =
                order.Payments?.Where(x => x.Status != PaymentStatus.Cancelled && x.Status != PaymentStatus.Storned)
                    ?.ToList() ?? new List<IPaymentItem>();
            if (paymentsO.Any())
            {
                paymentsO.ForEach(p =>
                {
                    var type = EnumPaymentType.Other;
                    if (p.Type.PrintCheque && p.Type.Kind == PaymentTypeKind.Cash)
                        type = EnumPaymentType.Cash;
                    else if (p.Type.PrintCheque && p.Type.Kind != PaymentTypeKind.Cash)
                        type = EnumPaymentType.Card;

                    payments ??= new List<KeyValueClass>();
                    var pay = payments.FirstOrDefault(x => x.Id == p.Type.Id
                                                           && x.Type == type
                    );
                    if (pay is null)
                    {
                        pay = new KeyValueClass
                        {
                            Name = p.Type.Name,
                            Value = 0,
                            Type = type,
                            Id = p.Type.Id,
                        };
                        payments.Add(pay);
                    }

                    pay.Value += p.Sum;
                });
            }


            EnumOrderStatusDto status = order.Status switch
            {
                OrderStatus.New => EnumOrderStatusDto.New,
                OrderStatus.Bill => EnumOrderStatusDto.Bill,
                OrderStatus.Closed => EnumOrderStatusDto.Closed,
                _ => throw new ArgumentOutOfRangeException()
            };

            var isDelivery = false;
            var waiter = order?.Waiter?.Name ?? string.Empty;
            var waiterId = $"{(order?.Waiter?.Id ?? Guid.Empty)}";
            var cashier = order.Cashier?.Name ?? string.Empty;
            var cashierId = $"{(order?.Cashier?.Id ?? Guid.Empty)}";

            string deliverySeriveType = null;
            string client = null;
            string phone = null;
            var address = string.Empty;
            IDeliveryOrder deliveryOrder = null;
            if (order is IDeliveryOrder dorder)
            {
                deliveryOrder = dorder;
                isDelivery = true;
                deliverySeriveType = deliveryOrder.OrderType?.OrderServiceType.ToString();
                waiter = deliveryOrder.DeliveryOperator?.Name ?? string.Empty;
                client =
                    $"{deliveryOrder.Client?.Surname ?? string.Empty} {deliveryOrder.Client?.Name ?? string.Empty}";
                phone = deliveryOrder.Phone;

                address = GenerateDeliveryAddress(deliveryOrder);
            }


            result = new PluginToServerOrderDetails
            {
                DeliveryCancelTime = deliveryOrder?.CancelTime,
                DeliveryConfirmTime = deliveryOrder?.ConfirmTime,
                DeliveryCreateTime = deliveryOrder?.CreateTime,
                DeliveryPrintTime = deliveryOrder?.PrintTime,
                DeliveryOpenTime = deliveryOrder?.OpenTime,
                DeliverySendTime = deliveryOrder?.SendTime,
                DeliveryExpectedDeliverTime = deliveryOrder?.ExpectedDeliverTime,
                DeliveryCookingFinishTime = deliveryOrder?.CookingFinishTime,
                DeliveryDeliveryCloseTime = deliveryOrder?.DeliveryCloseTime,
                DeliveryPredictedCookingCompleteTime = deliveryOrder?.PredictedCookingCompleteTime,
                DeliveryActualDeliverTime = deliveryOrder?.ActualDeliverTime,
                DeliveryPredictedDeliveryTime = deliveryOrder?.PredictedDeliveryTime,
                DeliveryDuration = deliveryOrder?.Duration,
                DeliveryExpectedDuration = deliveryOrder?.ExpectedDuration,
                DeliveryAddress = address,


                ReserveGuestComingTime = reserveGuestComingTime,
                ReserveDuration = reserveDuration,
                ReserveEstimatedStartTime = reserveEstimatedStartTime,

                ReserveClientName = reserveClientName,
                ReserveClientPhone = reserveClientPhone,
                DeliveryClient = client,
                DeliveryPhone = phone,
                GuestCount = order.Guests.Count,
                IsDelivery = isDelivery,
                DeliveryServiceType = deliverySeriveType,
                Tables = order?.Tables?.GetTablesAsString(),
                OrderNum = order.Number,
                Floor = order.Tables.Any()
                    ? order.Tables.First().RestaurantSection?.Name ?? string.Empty
                    : string.Empty,
                Waiter = waiter,
                WaiterId = waiterId,
                Cashier = cashier,
                CashierId = cashierId,
                OpenTime = order.OpenTime,
                CloseTime = order.CloseTime,
                BillTime = order.BillTime,
                Revenue = order.ResultSum,
                IsBanquet = order.IsBanquetOrder ||
                            (!string.IsNullOrEmpty(reserveClientName) && !string.IsNullOrEmpty(reserveClientPhone)),
                Discounts = discounts,
                Surcharges = surcharges,
                Payments = payments,
                Tips = tips,
                OrderStatus = status,
            };
            if (order.Items.Any(x => x is { Deleted: false }))
            {
                result.Items = new List<PluginToServerOrderDetailsItem>();

                foreach (var item in order.Items.Where(x => x is { Deleted: false }))
                {
                    switch (item)
                    {
                        case IOrderProductItem productItem:
                            result.Items.Add(GenerateOrderProductItem(productItem));
                            break;
                        case IOrderServiceItem serviceItem:
                            result.Items.Add(GenerateOrderServiceItem(serviceItem));
                            break;
                        case IOrderCompoundItem compoundItem:
                            var compoundAmount = compoundItem.SecondaryComponent == null
                                ? compoundItem.Amount
                                : 0.5M * compoundItem.Amount;
                            if (compoundItem.PrimaryComponent != null)
                            {
                                result.Items.Add(GenerateCompoundComponentItem(compoundAmount, compoundItem, true));
                            }

                            if (compoundItem.SecondaryComponent != null)
                            {
                                result.Items.Add(GenerateCompoundComponentItem(compoundAmount, compoundItem));
                            }

                            break;
                    }
                }
            }

            // PluginContext.Log.Debug($"GenerateOrder :: finished.",!PluginHelpers.IsDeveloperMode);
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GenerateOrder :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GenerateOrder :: {ex.Message}");
        }


        return result;
    }

    private string GenerateDeliveryAddress(IDeliveryOrder deliveryOrder)
    {
        var address = string.Empty;
        if (deliveryOrder.Address is null) return address;

        var addressList = new List<string>();
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Index))
            addressList.Add(deliveryOrder.Address?.Index);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Region?.Name))
            addressList.Add(deliveryOrder.Address?.Region?.Name);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Street?.City?.Name))
            addressList.Add(deliveryOrder.Address?.Street?.City?.Name);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.House))
            addressList.Add(deliveryOrder.Address?.House);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Building))
            addressList.Add(deliveryOrder.Address?.Building);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Entrance))
            addressList.Add(deliveryOrder.Address?.Entrance);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Floor))
            addressList.Add(deliveryOrder.Address?.Floor);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Flat))
            addressList.Add(deliveryOrder.Address?.Flat);
        if (!string.IsNullOrEmpty(deliveryOrder.Address?.Doorphone))
            addressList.Add(deliveryOrder.Address?.Doorphone);
        address = string.Join(", ", addressList);
        return address;
    }

    private PluginToServerOrderDetailsItem GenerateCompoundComponentItem(decimal compoundAmount,
        IOrderCompoundItem compoundItem, bool isPrimary = false)
    {
        var item = isPrimary ? compoundItem.PrimaryComponent : compoundItem.SecondaryComponent;
        var itemStatus = compoundItem.Status switch
        {
            OrderItemStatus.Added => EnumOrderItemStatusDto.Added,
            OrderItemStatus.PrintedNotCooking => EnumOrderItemStatusDto.PrintedNotCooking,
            OrderItemStatus.CookingStarted => EnumOrderItemStatusDto.CookingStarted,
            OrderItemStatus.CookingCompleted => EnumOrderItemStatusDto.CookingCompleted,
            OrderItemStatus.Served => EnumOrderItemStatusDto.Served,
        };


        var itemDto = new PluginToServerOrderDetailsItem
        {
            PrintTime = compoundItem.PrintTime,
            Status = itemStatus,
            Name = string.IsNullOrEmpty(item.ProductCustomName) ? item.Product.Name : item.ProductCustomName,
            CookingTime = compoundItem.CookingTime,
            ServeTime = compoundItem.ServeTime,
            CookingStartTime = compoundItem.CookingStartTime,
            CookingFinishTime = compoundItem.CookingStartTime,
            Amount = compoundAmount,
            Size = compoundItem.Size?.Name ?? string.Empty,
            ResultSum = item.ResultSum,
            Price = item.Price,
        };
        if (item.Modifiers.Any())
        {
            itemDto.Modifiers ??= new List<PluginToServerOrderDetailsItemModifier>();

            foreach (var modifier in item.Modifiers)
                itemDto.Modifiers.Add(GenerateModifier(modifier));
        }

        if (compoundItem.CommonModifiers.Any() && isPrimary)
        {
            itemDto.Modifiers ??= new List<PluginToServerOrderDetailsItemModifier>();
            foreach (var modifier in compoundItem.CommonModifiers)
                itemDto.Modifiers.Add(GenerateModifier(modifier));
        }

        return itemDto;
    }

    private PluginToServerOrderDetailsItem GenerateOrderServiceItem(IOrderServiceItem serviceItem)
    {
        var itemStatus = serviceItem.Status switch
        {
            OrderItemStatus.Added => EnumOrderItemStatusDto.Added,
            OrderItemStatus.PrintedNotCooking => EnumOrderItemStatusDto.PrintedNotCooking,
            OrderItemStatus.CookingStarted => EnumOrderItemStatusDto.CookingStarted,
            OrderItemStatus.CookingCompleted => EnumOrderItemStatusDto.CookingCompleted,
            OrderItemStatus.Served => EnumOrderItemStatusDto.Served,
        };

        var itemDto = new PluginToServerOrderDetailsItem
        {
            Name = string.IsNullOrEmpty(serviceItem.ServiceCustomName)
                ? serviceItem.Service.Name
                : serviceItem.ServiceCustomName,
            TimeLimit = serviceItem.TimeLimit,
            Amount = 1M,
            ResultSum = serviceItem.Cost,
            Price = serviceItem.Price,
            Status = itemStatus,
        };
        if (serviceItem.Periods.Any())
        {
            itemDto.Modifiers = new List<PluginToServerOrderDetailsItemModifier>();
            foreach (var period in serviceItem.Periods)
            {
                itemDto.Modifiers.Add(new PluginToServerOrderDetailsItemModifier
                {
                    Name = string.IsNullOrEmpty(period.ServiceCustomName)
                        ? period.Service.Name
                        : period.ServiceCustomName,
                    Amount = 1M,
                    ResultSum = period.Cost,
                    Price = period.Price,
                });
            }
        }

        return itemDto;
    }

    private PluginToServerOrderDetailsItem GenerateOrderProductItem(IOrderProductItem productItem)
    {
        var itemStatus = productItem.Status switch
        {
            OrderItemStatus.Added => EnumOrderItemStatusDto.Added,
            OrderItemStatus.PrintedNotCooking => EnumOrderItemStatusDto.PrintedNotCooking,
            OrderItemStatus.CookingStarted => EnumOrderItemStatusDto.CookingStarted,
            OrderItemStatus.CookingCompleted => EnumOrderItemStatusDto.CookingCompleted,
            OrderItemStatus.Served => EnumOrderItemStatusDto.Served,
        };


        var itemDto = new PluginToServerOrderDetailsItem
        {
            Name = string.IsNullOrEmpty(productItem.ProductCustomName)
                ? productItem.Product.Name
                : productItem.ProductCustomName,
            CookingTime = productItem.CookingTime,
            ServeTime = productItem.ServeTime,
            CookingStartTime = productItem.CookingStartTime,
            CookingFinishTime = productItem.CookingFinishTime,
            Amount = productItem.Amount,
            Size = productItem.Size?.Name ?? string.Empty,
            ResultSum = productItem.ResultSum,
            Price = productItem.OpenPrice ?? productItem.Price,
            Status = itemStatus,
        };
        if (productItem.AssignedModifiers.Any())
        {
            itemDto.Modifiers = new List<PluginToServerOrderDetailsItemModifier>();
            foreach (var modifier in productItem.AssignedModifiers)
            {
                itemDto.Modifiers.Add(GenerateModifier(modifier));
            }
        }

        return itemDto;
    }

    private PluginToServerOrderDetailsItemModifier GenerateModifier(IOrderModifierItem modifier)
    {
        return new PluginToServerOrderDetailsItemModifier
        {
            Name = string.IsNullOrEmpty(modifier.ProductCustomName)
                ? modifier.Product.Name
                : modifier.ProductCustomName,
            Amount = modifier.Amount,
            ResultSum = modifier.ResultSum,
            Price = modifier.Price,
        };
    }


    private IPluginToServer GenerateTopTenMealsByRevenue(List<IOrder> ord)
    {
        PluginToServerTopTenMealsByRevenue result = null;

        try
        {
            result = new PluginToServerTopTenMealsByRevenue();
            var myOrders = ord.Where(x =>
                x.Tables.FirstOrDefault()?.RestaurantSection?.TerminalsGroup?.Id == PluginHelpers.GroupName.Id
            )?.ToList();
            var products = new List<PluginToServerTopTenMealsByRevenueProduct>();
            foreach (var order in myOrders)
            {
                foreach (var item in order.Items.Where(x => !x.Deleted))
                {
                    switch (item)
                    {
                        case IOrderProductItem productItem:
                            AddTopTenMeals(productItem.Product.Name, productItem.Product.Number, productItem.ResultSum,
                                productItem.Amount, ref products);
                            if (productItem.AssignedModifiers.Any())
                            {
                                foreach (var modifier in productItem.AssignedModifiers)
                                {
                                    AddTopTenMeals(modifier.Product.Name, modifier.Product.Number,
                                        modifier.ResultSum, modifier.Amount, ref products);
                                }
                            }

                            break;
                        case IOrderServiceItem serviceItem:
                            AddTopTenMeals(serviceItem.Service.Name, serviceItem.Service.Number, serviceItem.Cost, 1M,
                                ref products);
                            break;
                        case IOrderCompoundItem compoundItem:
                            var amount = compoundItem.SecondaryComponent is null
                                ? compoundItem.Amount
                                : 0.5M * compoundItem.Amount;
                            if (compoundItem.CommonModifiers.Any())
                            {
                                foreach (var modifier in compoundItem.CommonModifiers)
                                {
                                    AddTopTenMeals(modifier.Product.Name, modifier.Product.Number,
                                        modifier.ResultSum, modifier.Amount, ref products);
                                }
                            }

                            if (compoundItem.PrimaryComponent is IOrderCompoundItemComponent primaryComponent)
                            {
                                AddTopTenMeals(primaryComponent.Product.Name, primaryComponent.Product.Number,
                                    primaryComponent.ResultSum,
                                    amount, ref products);
                                if (primaryComponent.Modifiers.Any())
                                {
                                    foreach (var modifier in primaryComponent.Modifiers)
                                    {
                                        AddTopTenMeals(modifier.Product.Name, modifier.Product.Number,
                                            modifier.ResultSum, modifier.Amount, ref products);
                                    }
                                }
                            }

                            if (compoundItem.SecondaryComponent is IOrderCompoundItemComponent secondaryComponent)
                            {
                                AddTopTenMeals(secondaryComponent.Product.Name, secondaryComponent.Product.Number,
                                    secondaryComponent.ResultSum,
                                    amount, ref products);
                                if (secondaryComponent.Modifiers.Any())
                                {
                                    foreach (var modifier in secondaryComponent.Modifiers)
                                    {
                                        AddTopTenMeals(modifier.Product.Name, modifier.Product.Number,
                                            modifier.ResultSum, modifier.Amount, ref products);
                                    }
                                }
                            }

                            break;
                    }
                }
            }

            result.Products = products.OrderByDescending(x => x.Revenue).Take(10).ToList();
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"PluginToServerTopTenMealsByRevenue :: {ex.Message}");
        }

        return result;
    }


    private void AddTopTenMeals(string name, string code, decimal sum, decimal amount,
        ref List<PluginToServerTopTenMealsByRevenueProduct> products)
    {
        var product = products.FirstOrDefault(x => x.Code == code);
        if (product == null)
        {
            product = new PluginToServerTopTenMealsByRevenueProduct
            {
                Name = name,
                Code = code,
                Revenue = 0M,
                Count = 0M,
            };
            products.Add(product);
        }

        product.Revenue += sum;
        product.Count += amount;
    }


    /// <summary>
    /// Отчет по высокорисковым операциям
    /// </summary>
    /// <returns></returns>
    private IPluginToServer GenerateHighRiskOperations()
    {
        PluginToServerHighRiskOperation result = null;
        try
        {
            PluginContext.Log.Debug($"GenerateHighRiskOperations :: started");

            var hrSource = _context.HighRiskOperationList;
            IEnumerable<HighRiskOperation> hr = _context.Shift == null
                ? Enumerable.Empty<HighRiskOperation>()
                : hrSource.Where(x => x.ShiftId == _context.Shift.Id);

            if (hr.Any())
            {
                result = new PluginToServerHighRiskOperation();

                foreach (var htTgId in hr.GroupBy(x => x.TerminalsGroupId))
                {
                    foreach (var userGroup in htTgId.Where(x => x.User != null).GroupBy(x => x.UserId))
                    {
                        var terminalsGroup = result.TerminalsGroups.FirstOrDefault(x => x.Id == htTgId.Key);
                        if (terminalsGroup == null)
                            continue;

                        var user = userGroup.Select(x => x.User).FirstOrDefault(u => u != null);
                        if (user == null)
                            continue;

                        foreach (var operation in userGroup.OrderBy(x => x.Date))
                        {
                            var userDb = terminalsGroup.Waiters.FirstOrDefault(x => x.Id == user.UserId);
                            if (userDb == null)
                            {
                                userDb = new PluginToServerHighRiskOperationTerminalsGroupWaiter
                                {
                                    Id = user.UserId,
                                    Name = user.UserName,
                                };
                                terminalsGroup.Waiters.Add(userDb);
                            }

                            var operations = userDb.Operations.FirstOrDefault(x => x.Name.Equals(operation.Action)
                                && operation.TerminalsGroupId == PluginHelpers.GroupName.Id
                            );
                            if (operations == null)
                            {
                                operations = new PluginToServerHighRiskOperationTerminalsGroupWaiterOperation
                                {
                                    Name = operation.Action,
                                    Count = 0,
                                };
                                userDb.Operations.Add(operations);
                            }

                            operations.Count++;
                            operations.LastActionDate = operation.Date;
                        }
                    }
                }

                result.RemoveEmptyTerminalsGroups();
            }

            PluginContext.Log.Debug($"GenerateHighRiskOperations :: finished");
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GenerateHighRiskOperations :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GenerateHighRiskOperations :: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Отчет по блюдам в стоп-листе
    /// </summary>
    /// <param name="getProductsRemainingAmounts"></param>
    /// <returns></returns>
    private IPluginToServer GenerateStopListRemainingMeals(Dictionary<IProduct, decimal> getProductsRemainingAmounts)
    {
        PluginToServerStopListRemainingMeals result = null;
        PluginContext.Log.Debug($"GenerateStopListRemainingMeals :: started");
        try
        {
            result = new PluginToServerStopListRemainingMeals
            {
                Products = getProductsRemainingAmounts.Select(x =>
                    new PluginToServerStopListRemainingMealsStopListProducts
                    {
                        Id = x.Key.Id,
                        Name = x.Key.Name,
                        Price = x.Key.Price,
                        Amount = x.Value,
                    }).ToList(),
            };
            PluginContext.Log.Debug($"GenerateStopListRemainingMeals :: finished");
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GenerateHighRiskOperations :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GenerateHighRiskOperations :: {ex.Message}");
        }

        return result;
    }

    #endregion


    private HorecaSqlite _context;

    public void SetDependecies(HorecaSqlite context)
    {
        _context = context;
        PluginContext.Log.Debug($"SetDependecies :: started");
    }

    // Методы реконнекта удалены - реконнект теперь управляется через SocketConnectionManager
}