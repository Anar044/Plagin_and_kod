using Microsoft.Extensions.DependencyInjection;
using Resto.Front.Api.Data.Device;
using Resto.Front.Api.Data.Device.Results;
using Resto.Front.Api.Data.Device.Tasks;
using Resto.Front.Api.Data.Security;
using Resto.Front.Api.Devices;
using Resto.Front.Api.HorecaControlPlugin.Dto.Events;
using Resto.Front.Api.UI;
using System;
using Resto.Front.Api.HorecaControlPlugin.Core.Application.Services.Interfaces;

namespace Resto.Front.Api.HorecaControlPlugin;

public class HorecaControlChequeTaskProcessor : IChequeTaskProcessor
{
    private readonly IEventPublisher _eventPublisher;
    private decimal _cashRest = 0;

    public HorecaControlChequeTaskProcessor(IServiceProvider serviceProvider)
    {
        _eventPublisher = serviceProvider.GetRequiredService<IEventPublisher>();
    }

    public void BeforePayIn(ICashRegisterInfo device, decimal sumToPayIn, bool isCloseSession, IUser cashier,
        IViewManager viewManager)
    {
    }

    public void BeforePayOut(ICashRegisterInfo device, decimal availableSum, ref decimal sumToPayOut,
        bool isCloseSession,
        IUser cashier, IViewManager viewManager)
    {
    }

    public BeforeDoCheckActionResult BeforeDoCheckAction(ChequeTask chequeTask, ICashRegisterInfo device,
        CashRegisterChequeExtensions chequeExtensions, IViewManager viewManager)
    {
        return new BeforeDoCheckActionResult();
    }


    public void BeforeZReport(ICashRegisterInfo device, decimal cashRest, IUser authUser, IViewManager viewManager)
    {
        _cashRest = cashRest;
    }

    public void BeforeXReport(ICashRegisterInfo device, IUser authUser, IViewManager viewManager)
    {
    }


    public void AfterDoCheckAction(ChequeTask chequeTask, PostResult result, ICashRegisterInfo device,
        IViewManager viewManager)
    {
        try
        {
            //var order = PluginContext.Operations.TryGetOrderById(chequeTask.OrderId);
            //_client.PluginToServerEvent(new PluginToServerEvent
            //{
            //    PluginEventType = EnumPluginEventType.ClosingOrder,
            //    Data = new PluginToServerEventOrder
            //    {
            //        Table = chequeTask.TableNumber,
            //        OrderNum = chequeTask.OrderNumber,
            //        Floor = order.Tables[0]?.RestaurantSection?.Name ?? string.Empty,
            //        Waiter = order.Waiter?.Name ?? string.Empty,
            //        Cashier = order.Cashier?.Name ?? string.Empty,
            //        OpenTime = order.OpenTime,
            //        CloseTime = order.CloseTime,
            //        BillTime = order.BillTime,
            //        Revenue = chequeTask.ResultSum,
            //    }
            //});
        }
        catch
        {
        }
    }

    public void AfterZReport(ICashRegisterInfo device, PostResult result, IUser authUser, IViewManager viewManager)
    {
        try
        {
            var cafeSessionData = PluginContext.Operations.TryGetCafeSessionByCashRegister(device);
            _eventPublisher.PublishEvent(new PluginToServerEvent
            {
                PluginEventType = EnumPluginEventType.CloseCashRegisterShift,
                Data = new PluginToServerEventOpenCloseSession
                {
                    OpenTime = cafeSessionData.OpenTime,
                    CloseTime = DateTime.Now,
                    Revenue = _cashRest,
                    ShiftNumber = cafeSessionData.Number
                }
            });
            _cashRest = 0;
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"AfterZReport :: {ex.Message}", ex);
        }
    }

    public void AfterXReport(ICashRegisterInfo device, PostResult result, IViewManager viewManager)
    {
    }

    public void AfterPayIn(ICashRegisterInfo device, decimal sum, PostResult result, IViewManager viewManager)
    {
    }

    public void AfterPayOut(ICashRegisterInfo device, decimal sum, PostResult result, IViewManager viewManager)
    {
    }

    #region API V8 only

    public void BeforeOpenSession(ICashRegisterInfo device, IUser authUser, IViewManager viewManager)
    {
    }

    public void AfterOpenSession(ICashRegisterInfo device, PostResult result, IViewManager viewManager)
    {
    }

    #endregion


    public bool IsPrimary { get; }
}