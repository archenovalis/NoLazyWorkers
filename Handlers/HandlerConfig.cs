using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
using NoLazyWorkers.General;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.General.GeneralExtensions;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.Handlers.HandlerExtensions;

namespace NoLazyWorkers.Handlers
{
  public static class HandlerExtensions
  {
    // Class: TransferRequest
    // Purpose: Represents a request to transfer items between locations, supporting multiple pickup and delivery slots.
    // Fields:
    //   - Item: The item to transfer.
    //   - Quantity: The number of items to transfer.
    //   - PickupLocation: The source entity (null for inventory routes).
    //   - PickupSlots: The list of slots to pick up items from.
    //   - DeliveryLocation: The destination entity.
    //   - DeliverySlots: The list of slots to deliver items to.
    public class TransferRequest
    {
      public ItemInstance Item { get; }
      public int Quantity { get; }
      public ItemSlot InventorySlot { get; }
      public ITransitEntity PickupLocation { get; }
      public List<ItemSlot> PickupSlots { get; }
      public ITransitEntity DeliveryLocation { get; }
      public List<ItemSlot> DeliverySlots { get; }

      public TransferRequest(ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickupLocation, List<ItemSlot> pickupSlots,
          ITransitEntity deliveryLocation, List<ItemSlot> deliverySlots)
      {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
        InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
        PickupLocation = pickupLocation; // Can be null for inventory routes
        DeliveryLocation = deliveryLocation ?? throw new ArgumentNullException(nameof(deliveryLocation));

        // Validate pickupSlots
        PickupSlots = pickupSlots != null
            ? pickupSlots.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList()
            : new List<ItemSlot>();
        if (pickupSlots != null && pickupSlots.Any(slot => slot == null || slot.ItemInstance == null || slot.Quantity <= 0))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"TransferRequest: Filtered out {pickupSlots.Count - PickupSlots.Count} invalid pickup slots (null, no item, or empty)",
              DebugLogger.Category.Handler);
        }
        if (PickupLocation == null && PickupSlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"TransferRequest: No valid pickup slots for inventory route with item {item.ID}",
              DebugLogger.Category.Handler);
          throw new ArgumentException("No valid pickup slots for inventory route");
        }

        // Validate deliverySlots
        DeliverySlots = deliverySlots != null
            ? deliverySlots.Where(slot => slot != null).ToList()
            : throw new ArgumentNullException(nameof(deliverySlots));
        if (deliverySlots != null && deliverySlots.Any(slot => slot == null))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"TransferRequest: Filtered out {deliverySlots.Count - DeliverySlots.Count} null delivery slots",
              DebugLogger.Category.Handler);
        }
        if (DeliverySlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"TransferRequest: No valid delivery slots for item {item.ID} to {deliveryLocation.Name}",
              DebugLogger.Category.Handler);
          throw new ArgumentException("No valid delivery slots");
        }
      }
    }
  }
}