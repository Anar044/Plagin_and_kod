using Microsoft.Extensions.DependencyInjection;
using Resto.Front.Api.Attributes;
using Resto.Front.Api.Attributes.JetBrains;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.Payments;
using Resto.Front.Api.Data.Security;
using Resto.Front.Api.Exceptions;
using Resto.Front.Api.HorecaControlPlugin.Core.Application.Services;
using Resto.Front.Api.HorecaControlPlugin.Core.Application.Services.Interfaces;
using Resto.Front.Api.HorecaControlPlugin.Core.Infrastructure.Communication;
using Resto.Front.Api.HorecaControlPlugin.Core.Infrastructure.Communication.Interfaces;
using Resto.Front.Api.HorecaControlPlugin.Core.Infrastructure.Persistence.Repositories;
using Resto.Front.Api.HorecaControlPlugin.Core.Infrastructure.Persistence.Repositories.Interfaces;
using Resto.Front.Api.HorecaControlPlugin.Dto.Events;
using Resto.Front.Api.HorecaControlPlugin.Helpers;
using Resto.Front.Api.HorecaControlPlugin.Notifiers;
using Resto.Front.Api.HorecaControlPlugin.Sqlite;
using Resto.Front.Api.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

//using Resto.Front.Api.HorecaControlPlugin.TestDto;

namespace Resto.Front.Api.HorecaControlPlugin
{
    [UsedImplicitly]
    [PluginLicenseModuleId(0021016318)]
    public sealed class HorecaControl : IFrontPlugin
    {
        private readonly CompositeDisposable subscriptions = new();
        private readonly ServerEventHandler _eventHandler;
        private readonly IEventPublisher _eventPublisher;
        private IServiceProvider _serviceProvider;
        private readonly HorecaSqlite _context;


        private DebugSettings debugSettings;

        static HorecaControl()
        {
            // До первого обращения к HorecaSqlite / linq2db DataConnection.
            AsyncInterfacesBootstrap.Attach();
        }

        public HorecaControl()
        {
            try
            {
#if V8P5
                var path = Path.Combine(UnmanagedDllPathHelper.StorageDirectory, "dll");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                UnmanagedDllPathHelper.ExtractDllFromResources(path, "SQLite.Interop.dll");
                UnmanagedDllPathHelper.SetDllDirectoryCPlusPlus(path);
#endif
                #region Start data

                ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls |
                (SecurityProtocolType)3072;


            PluginContext.Log.Info("All exist payments Ids:");
            var existPayments = PluginContext.Operations.GetPaymentTypes().Where(x => x.IsEnabled && !x.IsHidden)
                .OrderBy(x => x.Name)?.ToList() ?? new List<IPaymentType>();

            foreach (var payment in existPayments)
            {
                PluginContext.Log.Info($" => '{payment.Name}': {payment.Id} / {payment.Kind}");
            }

            PluginHelpers.ExcludedPayments = new List<Guid>();

            foreach (var id in Properties.Settings.Default.ExcludedPayments)
            {
                if (Guid.TryParse(id, out Guid guid))
                {
                    var ok = existPayments.FirstOrDefault(x => x.Id == guid);
                    if (ok != null)
                    {
                        PluginContext.Log.Info($"Payment type {ok.Name} add to control list.");
                        PluginHelpers.ExcludedPayments.Add(guid);
                    }
                    else
                    {
                        PluginContext.Log.Warn($"Payment type with guid {guid} not found.");
                    }
                }
                else
                {
                    PluginContext.Log.Warn($"String {id} is can't be parsed like GUID.");
                }
            }

            debugSettings = DebugSettings.GetDebugSettings();

            if ((debugSettings?.DebugSecretString ?? string.Empty).Equals("C1s$0%44a"))
            {
                PluginHelpers.IsDeveloperMode = true;
            }


                var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                var fileVersion =
                    FileVersionInfo.GetVersionInfo(assemblyLocation).FileVersion + (PluginHelpers.IsDeveloperMode
                        ? "-dev"
                        : "-prod");


                var configXmlPath = Path.Combine(PluginContext.Integration.GetConfigsDirectoryPath(), "..", "..",
                    "config.xml");

                var configText = File.ReadAllText(configXmlPath);
                var xmlDoc = XDocument.Parse(configText);
                var serverUrl = xmlDoc.GetElement("serverUrl");

                if (string.IsNullOrEmpty(serverUrl.Value))
                    throw new ArgumentNullException("Server Url is null");
                var group = PluginContext.Operations.GetHostTerminalsGroup();
                var department = PluginContext.Operations.GetHostRestaurant();
                var currencyCode = department.Currency.ShortNameForGui;


                var pluginConfigStorage = FileConfig.GetConfig();
                var pluginConfigIsolatedStorage = FileConfig.GetConfigStorageConfig();


                FileConfig pluginFileConfig = null;
                if (pluginConfigStorage?.PluginId is not null && pluginConfigIsolatedStorage?.PluginId is not null)
                {
                    pluginFileConfig = pluginConfigStorage;
                }
                else if (pluginConfigStorage?.PluginId is null && pluginConfigIsolatedStorage?.PluginId is not null)
                {
                    pluginFileConfig = pluginConfigIsolatedStorage;
                }
                else if (pluginConfigStorage?.PluginId is not null && pluginConfigIsolatedStorage?.PluginId is null)
                {
                    pluginFileConfig = pluginConfigStorage;
                }
                else if (pluginConfigStorage?.PluginId is null && pluginConfigIsolatedStorage?.PluginId is null)
                {
                    throw new Exception("Идентификатор плагина не задан");
                }

                if (pluginFileConfig is null)
                    throw new Exception("Идентификатор плагина не задан");


                var departmentId = department.DepartmentId;
                var iikoUrlString = serverUrl.Value;

                if (PluginHelpers.IsDeveloperMode)
                {
                    PluginContext.Log.Warn("Plugin running in DeveloperMode");
                    if (!string.IsNullOrEmpty(debugSettings?.DebugServerUrl))
                    {
                        iikoUrlString = debugSettings?.DebugServerUrl;
                        PluginContext.Log.Warn($"Dummy iiko server url = {iikoUrlString}");
                    }

                    if (debugSettings?.DebugDepartmentId.GetValueOrDefault(Guid.Empty) != Guid.Empty)
                    {
                        departmentId = debugSettings.DebugDepartmentId.Value;
                        PluginContext.Log.Warn(
                            $"Dummy DepartmentId string = {debugSettings.DebugDepartmentId.Value}");
                    }

                    PluginContext.Log.Warn($"Dummy connection string = {debugSettings?.DebugSocketUrl}");
                }

                var config = new SocketIoConnectorConfig
                {
                    Login = "c4h4nG1R4nd1G0Rm4d37h1s4ppl1ca710nf0rsm4r7h0r3c4",
                    Password = "f33ls0rryf0rm4nwh0w1LLh4V370r34d7h37",

                    PluginId = pluginFileConfig.PluginId.Value,

                    DepartmentId = departmentId,
                    ServerUrl = iikoUrlString,

                    PluginName = Environment.MachineName,
                    GroupId = group.Id,
                    GroupName = $"{group.Name}",
                    DepartmentName = $"{department.Name}",
                    CurrencyCode = currencyCode,
                    Version = fileVersion
                };
                PluginHelpers.GroupName = group;
                PluginHelpers.DepartmentName = department;


                PluginContext.Log.Info($"Параметры плагина :");
                PluginContext.Log.Info($"ID плагина :                   {config.PluginId}");
                PluginContext.Log.Info($"Версия плагина :               {config.Version}");
                PluginContext.Log.Info($"Название ПК :                  '{config.PluginName}'");
                PluginContext.Log.Info($"Crm ID :                       {PluginHelpers.DepartmentName.CrmId}");
                PluginContext.Log.Info($"Iiko UUID :                    {PluginHelpers.DepartmentName.IikoUid}");
                PluginContext.Log.Info($"ID предприятия :               {config.DepartmentId}");
                PluginContext.Log.Info($"Название предприятия :         '{PluginHelpers.DepartmentName.Name}'");
                PluginContext.Log.Info($"ID терминальной группы :       {PluginHelpers.GroupName.Id}");
                PluginContext.Log.Info($"Название терминальной группы : '{PluginHelpers.GroupName.Name}'");
                PluginContext.Log.Info($"Код валюты :                   '{currencyCode}'");


                CultureInfo ci = new CultureInfo("ru-RU");
                Thread.CurrentThread.CurrentCulture = ci;
                Thread.CurrentThread.CurrentUICulture = ci;

                #endregion


                var services = new ServiceCollection();

                // Регистрируем базовые сервисы
                // Используем фабрику для обработки ошибок инициализации SQLite
                services.AddSingleton<HorecaSqlite>(sp =>
                {
                    try
                    {
                        PluginContext.Log.Info("HorecaControl :: Initializing SQLite database...");
                        var db = new HorecaSqlite();
                        PluginContext.Log.Info("HorecaControl :: SQLite database initialized successfully");
                        return db;
                    }
                    catch (Exception ex)
                    {
                        PluginContext.Log.Error($"HorecaControl :: CRITICAL ERROR: Failed to initialize SQLite database", ex);
                        PluginContext.Log.Error($"HorecaControl :: Exception type: {ex.GetType().FullName}");
                        PluginContext.Log.Error($"HorecaControl :: Error message: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            PluginContext.Log.Error($"HorecaControl :: Inner exception: {ex.InnerException.GetType().FullName} - {ex.InnerException.Message}");
                        }
                        PluginContext.Log.Error($"HorecaControl :: This is likely a SQLite.Interop.dll compatibility issue.");
                        PluginContext.Log.Error($"HorecaControl :: Please check:");
                        PluginContext.Log.Error($"HorecaControl ::   1. SQLite.Interop.dll exists in the output directory");
                        PluginContext.Log.Error($"HorecaControl ::   2. The correct architecture (x86/x64) is used");
                        PluginContext.Log.Error($"HorecaControl ::   3. All required SQLite native libraries are present");
                        throw;
                    }
                });

                // Регистрируем Repository
                services.AddSingleton<IRepository>(sp =>
                {
                    var db = sp.GetRequiredService<HorecaSqlite>();
                    return new SqliteRepository(db);
                });

                // Регистрируем SocketIO клиент через фабрику
                services.AddSingleton<SocketIOClient.SocketIO>(sp =>
                {
                    return SocketIOFactory.CreateClient(config, debugSettings);
                });

                // Регистрируем SocketConnectionManager
                services.AddSingleton<ISocketConnectionManager>(sp =>
                {
                    var client = sp.GetRequiredService<SocketIOClient.SocketIO>();
                    var repository = sp.GetRequiredService<IRepository>();
                    return new SocketConnectionManager(client, repository);
                });

                // Регистрируем ReportGenerator
                services.AddSingleton<IReportGenerator>(sp =>
                {
                    var repository = sp.GetRequiredService<IRepository>();
                    return new ReportGenerator(repository);
                });

                // Регистрируем EventPublisher
                services.AddSingleton<IEventPublisher>(sp =>
                {
                    var connectionManager = sp.GetRequiredService<ISocketConnectionManager>();
                    return new EventPublisher(connectionManager);
                });

                // Регистрируем Application Services
                services.AddSingleton<IOrderService>(sp =>
                {
                    var repository = sp.GetRequiredService<IRepository>();
                    return new OrderService(repository);
                });

                services.AddSingleton<IShiftService>(sp =>
                {
                    var repository = sp.GetRequiredService<IRepository>();
                    return new ShiftService(repository);
                });

                services.AddSingleton<IEventService>(sp =>
                {
                    var repository = sp.GetRequiredService<IRepository>();
                    return new EventService(repository);
                });

                // Регистрируем ServerEventHandler с зависимостями
                services.AddSingleton<ServerEventHandler>(sp =>
                {
                    var client = sp.GetRequiredService<SocketIOClient.SocketIO>();
                    var connectionManager = sp.GetRequiredService<ISocketConnectionManager>();
                    var reportGenerator = sp.GetRequiredService<IReportGenerator>();
                    var eventPublisher = sp.GetRequiredService<IEventPublisher>();
                    var repository = sp.GetRequiredService<IRepository>();
                    return new ServerEventHandler(config, debugSettings, client, connectionManager, reportGenerator, eventPublisher, repository);
                });

                _serviceProvider = services.BuildServiceProvider();

                _context = _serviceProvider.GetRequiredService<HorecaSqlite>();
                _eventHandler = _serviceProvider.GetRequiredService<ServerEventHandler>();
                _eventPublisher = _serviceProvider.GetRequiredService<IEventPublisher>();

                _context.OnStart();
                _eventHandler.SetDependecies(_context);

                subscriptions.Add(_serviceProvider as IDisposable);


                if (_context.Shift is null)
                {
                    PluginContext.Log.Info("Нет открытых смен в БД.");
                    var cafeSessions = PluginContext.Operations.GetCafeSessions();
                    PluginContext.Log.Info($"В iiko открытых смен {cafeSessions.Count}.");
                    if (cafeSessions.Any())
                    {
                        PluginContext.Log.Info("Открываем смену в БД.");
                        _context.OpenShift();
                        PluginContext.Log.Info("Смена в БД открыта.");
                    }
                }


                subscriptions.Add(new OrderChangeNotifier(_serviceProvider));
                subscriptions.Add(new ReserveChangeNotificator(_serviceProvider));
                subscriptions.Add(new DeliveryOrderChangeNotifier(_serviceProvider));
                subscriptions.Add(new KitchenChangeNotifier(_serviceProvider));
                subscriptions.Add(
                    PluginContext.Operations.RegisterChequeTaskProcessor(
                        new HorecaControlChequeTaskProcessor(_serviceProvider)));
                subscriptions.Add(
                    PluginContext.Notifications.CafeSessionOpening.Subscribe(CafeSessionOpeningSubscribe));
                subscriptions.Add(
                    PluginContext.Notifications.CafeSessionClosing.Subscribe(CafeSessionClosingSubscribe));
                subscriptions.Add(PluginContext.Notifications.BeforeOrderBill.Subscribe(BeforeOrderBillSubscribe));
                subscriptions.Add(
                    PluginContext.Notifications.OrderBillCancelled.Subscribe(OrderBillCancelledSubscribe));
                subscriptions.Add(
                    PluginContext.Notifications.BeforeDeleteNonPrintedItems.Subscribe(
                        BeforeDeleteNonPrintedItemsSubscribe));

                subscriptions.Add(new StopListChangeNotifier(_serviceProvider));

                subscriptions.Add(
                    PluginContext.Notifications.BeforeDeletePrintedItems.Subscribe(BeforeDeletePrintedItemsSubscribe));

                subscriptions.Add(
                    PluginContext.Notifications.UserSessionChanged.Subscribe(UserSessionChangedSubscribe));
                
                // Подключаемся через ConnectionManager напрямую
                var connectionManager = _serviceProvider.GetRequiredService<ISocketConnectionManager>();
                Task.Run(() => connectionManager.Connect());
            }
            catch (LicenseRestrictionException ex)
            {
                PluginContext.Log.Error($"LicenseRestrictionException : {ex.Message}", ex);
                return;
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"Exception : {ex.Message}", ex);
                return;
            }
            // StartTimer();
        }

        private void UserSessionChangedSubscribe(IReadOnlyList<IUser> readOnlyList)
        {
            foreach (var user in readOnlyList)
            {
                _eventPublisher.PublishEvent(new PluginToServerEvent
                {
                    PluginEventType = (user.IsSessionOpen)
                        ? EnumPluginEventType.EmployeesPersonalShiftOpen
                        : EnumPluginEventType.EmployeesPersonalShiftClosed,
                    Data = new PluginToServerEventUserState
                    {
                        IsSessionOpen = user.IsSessionOpen,
                        EmployeeName = user.Name,
                    }
                });
            }
        }

        private void BeforeDeletePrintedItemsSubscribe(
            (IOrder order, IReadOnlyCollection<IOrderRootItem> deletingItems, IReadOnlyCollection<IOrderModifierItem>
                deletingModifiers, IDeletionMethod dm, IUser user, IViewManager vm) obj)
        {
            try
            {
                if (obj.order.StornedOrderId != null)
                    return;
                var table = obj.order.Tables.GetTablesAsString();


                var orderNum = obj.order.Number;
                var floor = obj.order.Tables[0]?.RestaurantSection?.Name ?? string.Empty;
                var waiter = obj.order.Waiter?.Name ?? string.Empty;
                var cashier = obj.order.Cashier?.Name ?? string.Empty;
                var productName = string.Empty;
                var reasonWriteOff = obj.dm.RemovalType.Name;
                var reasonComment = (string.IsNullOrEmpty(obj.dm?.Comment) ? "" : obj.dm?.Comment);
                var sum = 0M;
                if (obj.deletingItems != null && obj.deletingItems.Any())
                {
                    foreach (var itm in obj.deletingItems)
                    {
                        if (itm.Deleted)
                            continue;

                        switch (itm)
                        {
                            case IOrderProductItem product:
                                productName = product.Product.Name;
                                sum = product.ResultSum;
                                break;
                            case IOrderServiceItem service:
                                productName = service.Service.Name;
                                sum = service.ResultSum;
                                break;
                            case IOrderCompoundItem compound:
                                var productBoth = new List<string>();
                                if (compound.PrimaryComponent is { } primary)
                                {
                                    productBoth.Add(primary.Product.Name);
                                    sum = primary.ResultSum;
                                }

                                if (compound.SecondaryComponent is { } secondary)
                                {
                                    productBoth.Add(secondary.Product.Name);
                                    sum += secondary.ResultSum;
                                }

                                productName = string.Join(", ", productBoth);
                                break;
                        }

                        if (!string.IsNullOrEmpty(productName))
                        {
                            _eventPublisher.PublishEvent(new PluginToServerEvent
                            {
                                PluginEventType = EnumPluginEventType.DeletionOfPrintedItem,
                                Data = new PluginToServerEventDeletionPrintedItem
                                {
                                    Tables = table,
                                    OrderNum = orderNum,
                                    Floor = floor,
                                    Waiter = waiter,
                                    Cashier = cashier,
                                    ProductName = productName,
                                    ProductType = EnumProductType.Product,
                                    ReasonWriteOff = reasonWriteOff,
                                    ReasonComment = reasonComment,
                                    Sum = sum,
                                }
                            });
                            _context.AddHighRiskOperation(obj.user, "deletingPrintedItem");
                        }
                    }
                }

                productName = string.Empty;
                sum = 0;
                if (obj.deletingModifiers != null && obj.deletingModifiers.Any())
                {
                    foreach (var itm in obj.deletingModifiers)
                    {
                        if (itm.Deleted)
                            continue;

                        switch (itm)
                        {
                            case IOrderModifierItem product:
                                productName = product.Product.Name;
                                sum = product.ResultSum;
                                break;
                        }

                        if (!string.IsNullOrEmpty(productName))
                        {
                            _eventPublisher.PublishEvent(new PluginToServerEvent
                            {
                                PluginEventType = EnumPluginEventType.DeletionOfNotPrintedItem,
                                Data = new PluginToServerEventDeletionPrintedItem
                                {
                                    Tables = table,
                                    OrderNum = orderNum,
                                    Floor = floor,
                                    Waiter = waiter,
                                    Cashier = cashier,
                                    ProductName = productName,
                                    ProductType = EnumProductType.Modifier,
                                    ReasonWriteOff = reasonWriteOff,
                                    ReasonComment = reasonComment,
                                    Sum = sum,
                                }
                            });
                            _context.AddHighRiskOperation(obj.user, "deletingPrintedItem");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"BeforeDeletePrintedItemsSubscribe :: {ex.Message}", ex);
            }
        }

        private void BeforeDeleteNonPrintedItemsSubscribe(
            (IOrder order, IReadOnlyCollection<IOrderRootItem> deletingItems, IReadOnlyCollection<IOrderModifierItem>
                deletingModifiers, IUser user, IViewManager vm) obj)
        {
            try
            {
                if (obj.order.StornedOrderId != null)
                    return;
                var table = obj.order.Tables.GetTablesAsString();
                var orderNum = obj.order.Number;
                var floor = obj.order.Tables[0]?.RestaurantSection?.Name ?? string.Empty;
                var waiter = obj.order.Waiter?.Name ?? string.Empty;
                var cashier = obj.order.Cashier?.Name ?? string.Empty;
                var productName = string.Empty;
                var sum = 0M;
                if (obj.deletingItems != null && obj.deletingItems.Any())
                {
                    foreach (var itm in obj.deletingItems)
                    {
                        if (itm.Deleted)
                            continue;

                        switch (itm)
                        {
                            case IOrderProductItem product:
                                productName = product.Product.Name;
                                sum = product.ResultSum;
                                break;
                            case IOrderServiceItem service:
                                productName = service.Service.Name;
                                sum = service.ResultSum;
                                break;
                            case IOrderCompoundItem compound:
                                var productBoth = new List<string>();
                                if (compound.PrimaryComponent is { } primary)
                                {
                                    productBoth.Add(primary.Product.Name);
                                    sum = primary.ResultSum;
                                }

                                if (compound.SecondaryComponent is { } secondary)
                                {
                                    productBoth.Add(secondary.Product.Name);
                                    sum += secondary.ResultSum;
                                }

                                productName = string.Join(", ", productBoth);

                                break;
                        }

                        if (!string.IsNullOrEmpty(productName))
                        {
                            _eventPublisher.PublishEvent(new PluginToServerEvent
                            {
                                PluginEventType = EnumPluginEventType.DeletionOfNotPrintedItem,
                                Data = new PluginToServerEventDeletionPrintedItem
                                {
                                    Tables = table,
                                    OrderNum = orderNum,
                                    Floor = floor,
                                    Waiter = waiter,
                                    Cashier = cashier,
                                    ProductName = productName,
                                    ProductType = EnumProductType.Product,
                                    Sum = sum,
                                }
                            });
                            _context.AddHighRiskOperation(obj.user, "deletingNonPrintedItem");
                        }
                    }
                }

                productName = string.Empty;
                sum = 0;
                if (obj.deletingModifiers != null && obj.deletingModifiers.Any())
                {
                    foreach (var itm in obj.deletingModifiers)
                    {
                        if (itm.Deleted)
                            continue;

                        switch (itm)
                        {
                            case IOrderModifierItem product:
                                productName = product.Product.Name;
                                sum = product.ResultSum;
                                break;
                        }

                        if (!string.IsNullOrEmpty(productName))
                        {
                            _eventPublisher.PublishEvent(new PluginToServerEvent
                            {
                                PluginEventType = EnumPluginEventType.DeletionOfNotPrintedItem,
                                Data = new PluginToServerEventDeletionPrintedItem
                                {
                                    Tables = table,
                                    OrderNum = orderNum,
                                    Floor = floor,
                                    Waiter = waiter,
                                    Cashier = cashier,
                                    ProductName = productName,
                                    ProductType = EnumProductType.Modifier,
                                    Sum = sum,
                                }
                            });
                            _context.AddHighRiskOperation(obj.user, "deletingNonPrintedItem");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"BeforeDeleteNonPrintedItemsSubscribe :: {ex.Message}", ex);
            }
        }


        private void OrderBillCancelledSubscribe((IOrder order, IUser user) obj)
        {
            try
            {
                _eventPublisher.PublishEvent(new PluginToServerEvent
                {
                    PluginEventType = EnumPluginEventType.CancellationOfGuestBill,
                    Data = new PluginToServerEventOrder
                    {
                        Tables = obj.order.Tables.GetTablesAsString(),
                        OrderNum = obj.order.Number,
                        Floor = obj.order.Tables[0]?.RestaurantSection?.Name ?? string.Empty,
                        Waiter = obj.order.Waiter?.Name ?? string.Empty,
                        Cashier = obj.order.Cashier?.Name ?? string.Empty,
                        Revenue = obj.order.ResultSum,

                        BillTime = DateTime.Now,
                    }
                });
                _context.AddHighRiskOperation(obj.user, "billCancelled");
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"OrderBillCancelledSubscribe :: {ex.Message}", ex);
            }
        }

        private void BeforeOrderBillSubscribe((IOrder order, IUser user, IOperationService os, IViewManager vm) obj)
        {
            if (obj.order.Status != OrderStatus.New)
                return;
            try
            {
                _eventPublisher.PublishEvent(new PluginToServerEvent
                {
                    PluginEventType = EnumPluginEventType.OrderGuestBill,
                    Data = new PluginToServerEventOrder
                    {
                        Tables = obj.order.Tables.GetTablesAsString(),
                        OrderNum = obj.order.Number,
                        Floor = obj.order.Tables[0]?.RestaurantSection?.Name ?? string.Empty,
                        Waiter = obj.order.Waiter?.Name ?? string.Empty,
                        Cashier = obj.order.Cashier?.Name ?? string.Empty,
                        Revenue = obj.order.ResultSum,
                        BillTime = DateTime.Now,
                    }
                });
                _context.AddHighRiskOperation(obj.user, "orderGuestBill");
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"BeforeOrderBillSubscribe :: {ex.Message}", ex);
            }
        }

        private void CafeSessionClosingSubscribe((IReceiptPrinter printer, IViewManager vm) obj)
        {
            try
            {
                _context.CloseShift(PluginContext.Operations.GetCurrentUser());
                _context.AddHighRiskOperation(PluginContext.Operations.GetCurrentUser(), "shiftClosed");
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"CafeSessionClosingSubscribe :: {ex.Message}", ex);
            }
        }

        private void CafeSessionOpeningSubscribe((IReceiptPrinter printer, IViewManager vm) obj)
        {
            try
            {
                var dt = DateTime.Now;
                _eventPublisher.PublishEvent(new PluginToServerEvent
                {
                    PluginEventType = EnumPluginEventType.OpenCashRegisterShift,
                    Data = new PluginToServerEventOpenCloseSession
                    {
                        OpenTime = dt,
                    }
                });

                // Каждое открытие кассовой смены iiko должно начинать новую логическую смену плагина
                // и обнулять события/рисковые операции. Если Close не пришёл — закрываем «сироту».
                if (_context.Shift != null && !_context.Shift.CloseTime.HasValue)
                {
                    _context.CloseShift(PluginContext.Operations.GetCurrentUser());
                }

                _context.OpenShift(dt, PluginContext.Operations.GetCurrentUser());

                _context.AddHighRiskOperation(PluginContext.Operations.GetCurrentUser(), "shiftOpened");

                var oldOrders = _context.ShiftOpenLoadNonClosingOrders();
                if (oldOrders.Any())
                {
                    oldOrders.ForEach(order =>
                    {
                        _eventPublisher.PublishEvent(new PluginToServerEvent
                        {
                            PluginEventType = EnumPluginEventType.SeveralOrderShifts,
                            Data = new PluginToServerEventOrder
                            {
                                // TODO доделать
                                Tables = string.Join(", ",
                                    (order.Tables?.Select(x => $"{(int)x.Value}")?.ToList()
                                     ?? new List<string>())),
                                OrderNum = order.Number,
                                Floor = order.Floor,
                                Waiter = order.WaiterName,
                                OpenTime = order.OpenTime,
                                CloseTime = order.CloseTime,
                                BillTime = order.BillTime,
                                Revenue = order.ResultSum,
                                IsBanquet = order.IsBanquet,
                                OrderShiftCount = order.ShiftCount,
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                PluginContext.Log.Error($"CafeSessionOpeningSubscribe :: {ex.Message}", ex);
            }
        }


        public void Dispose()
        {
            // StopTimer();
            // Сначала освобождаем обработчики событий, которые могут использовать базу данных
            subscriptions?.Dispose();
            // Затем освобождаем базу данных
            _context?.Dispose();
        }
    }
}