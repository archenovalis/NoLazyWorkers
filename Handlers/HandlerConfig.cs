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
using static NoLazyWorkers.Handlers.HandlerUtilities;

namespace NoLazyWorkers.Handlers
{
  public static class HandlerExtensions
  {
    public class TransferRequest
    {
      public ITransitEntity PickupLocation { get; }
      public ItemInstance PickupItem { get; }
      public ItemSlot PickupSlot { get; }
      public ITransitEntity DeliveryLocation { get; }
      public ItemInstance DeliveryItem { get; }
      public List<ItemSlot> DeliverySlots { get; }
      public int Quantity { get; }

      public TransferRequest(
          ITransitEntity pickupLocation, ItemInstance pickupItem, ItemSlot pickupSlot,
          ITransitEntity deliveryLocation, ItemInstance deliveryItem, List<ItemSlot> deliverySlots,
          int quantity)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"TransferRequest: Creating with pickupLocation={pickupLocation?.GetType().Name}, " +
            $"pickupItem={pickupItem?.ID}, pickupSlot={pickupSlot?.GetHashCode()}, " +
            $"deliveryLocation={deliveryLocation?.GetType().Name}, deliveryItem={deliveryItem?.ID}, " +
            $"deliverySlotsCount={deliverySlots?.Count}, quantity={quantity}",
            DebugLogger.Category.Handler);

        PickupLocation = pickupLocation ?? throw new ArgumentNullException(nameof(pickupLocation));
        PickupItem = pickupItem ?? throw new ArgumentNullException(nameof(pickupItem));
        PickupSlot = pickupSlot ?? throw new ArgumentNullException(nameof(pickupSlot));
        DeliveryLocation = deliveryLocation ?? throw new ArgumentNullException(nameof(deliveryLocation));
        DeliveryItem = deliveryItem ?? throw new ArgumentNullException(nameof(deliveryItem));
        DeliverySlots = deliverySlots ?? throw new ArgumentNullException(nameof(deliverySlots));
        Quantity = quantity;

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"TransferRequest: Created successfully for item {pickupItem.ID} from {pickupLocation.GetType().Name} to {deliveryLocation.GetType().Name}, quantity={quantity}",
            DebugLogger.Category.Handler);
      }
    }
  }

  public static class HandlerUtilities
  {
    public static readonly int MAX_ROUTES_PER_CYCLE = 5;
    public static readonly int PRIORITY_STATION_REFILL = 3;
    public static readonly int PRIORITY_LOADING_DOCK = 2;
    public static readonly int PRIORITY_SHELF_RESTOCK = 1;

    public static List<TransferRequest> FindItemsNeedingMovement(Packager npc)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"FindItemsNeedingMovement: Starting for NPC={npc?.fullName}, AssignedProperty={npc?.AssignedProperty?.GetType().Name}",
          DebugLogger.Category.Handler);

      if (npc == null || npc.AssignedProperty == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"FindItemsNeedingMovement: NPC is {(npc == null ? "null" : "not null")} and AssignedProperty is {(npc?.AssignedProperty == null ? "null" : "not null")}",
            DebugLogger.Category.Handler);
        return new List<TransferRequest>();
      }

      var property = npc.AssignedProperty;
      var requests = new List<TransferRequest>();
      var pickupGroups = new Dictionary<ITransitEntity, List<TransferRequest>>();

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Processing for property={property.GetType().Name}, LoadingDocksCount={property.LoadingDocks.Length}",
          DebugLogger.Category.Handler);

      // Step 1: Check loading docks
      for (int i = 0; i < property.LoadingDocks.Length; i++)
      {
        var dock = property.LoadingDocks[i];
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"FindItemsNeedingMovement: Checking LoadingDock index={i}, IsInUse={dock.IsInUse}",
            DebugLogger.Category.Handler);

        if (!dock.IsInUse)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Skipping LoadingDock index={i} as it is not in use",
              DebugLogger.Category.Handler);
          continue;
        }

        foreach (var slot in dock.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Skipping slot in LoadingDock index={i}, ItemInstance={(slot?.ItemInstance == null ? "null" : "not null")}, Quantity={slot?.Quantity}",
                DebugLogger.Category.Handler);
            continue;
          }

          var item = slot.ItemInstance;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Processing item={item.ID}, Quantity={slot.Quantity} in LoadingDock index={i}",
              DebugLogger.Category.Handler);

          var shelf = StorageUtilities.FindShelfForDelivery(npc, item);
          if (shelf == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: No suitable shelf found for item={item.ID} from LoadingDock index={i}",
                DebugLogger.Category.Handler);
            continue;
          }

          if (!npc.movement.CanGetTo(dock) || !npc.movement.CanGetTo(shelf))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: NPC cannot reach dock={dock.IsInUse} or shelf={shelf.GUID} for item={item.ID}",
                DebugLogger.Category.Handler);
            continue;
          }

          var deliverySlots = (shelf as ITransitEntity).ReserveInputSlotsForItem(item, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: No delivery slots available for item={item.ID} on shelf={shelf.GUID}",
                DebugLogger.Category.Handler);
            continue;
          }

          var quantity = Mathf.Min(slot.Quantity, (shelf as ITransitEntity).GetInputCapacityForItem(item, npc));
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"FindItemsNeedingMovement: Creating TransferRequest for item={item.ID}, quantity={quantity} from LoadingDock index={i} to shelf={shelf.GUID}, deliverySlotsCount={deliverySlots.Count}",
              DebugLogger.Category.Handler);

          var request = new TransferRequest(
              pickupLocation: dock,
              pickupItem: item,
              pickupSlot: slot,
              deliveryLocation: shelf,
              deliveryItem: item,
              deliverySlots: deliverySlots,
              quantity: quantity
          );

          if (!pickupGroups.ContainsKey(dock))
            pickupGroups[dock] = new List<TransferRequest>();
          pickupGroups[dock].Add(request);

          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Added TransferRequest to pickupGroups for dock index={i}, TotalRequests={pickupGroups[dock].Count}",
              DebugLogger.Category.Handler);
        }
      }

      // Step 2: Check "Any" shelves
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Checking AnyShelves, Count={StorageExtensions.AnyShelves.Count}",
          DebugLogger.Category.Handler);

      foreach (var anyShelf in StorageExtensions.AnyShelves)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"FindItemsNeedingMovement: Checking AnyShelf GUID={anyShelf.GUID}",
            DebugLogger.Category.Handler);

        if (!npc.movement.CanGetTo(anyShelf))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"FindItemsNeedingMovement: NPC cannot reach AnyShelf GUID={anyShelf.GUID}",
              DebugLogger.Category.Handler);
          continue;
        }

        foreach (var slot in anyShelf.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Skipping slot in AnyShelf GUID={anyShelf.GUID}, ItemInstance={(slot?.ItemInstance == null ? "null" : "not null")}, Quantity={slot?.Quantity}",
                DebugLogger.Category.Handler);
            continue;
          }

          var item = slot.ItemInstance;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Processing item={item.ID}, Quantity={slot.Quantity} in AnyShelf GUID={anyShelf.GUID}",
              DebugLogger.Category.Handler);

          var assignedShelf = StorageUtilities.FindShelfForDelivery(npc, item);
          if (assignedShelf == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: No assigned shelf found for item={item.ID} from AnyShelf GUID={anyShelf.GUID}",
                DebugLogger.Category.Handler);
            continue;
          }

          if (assignedShelf == anyShelf)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Skipping as assignedShelf is the same as AnyShelf GUID={anyShelf.GUID} for item={item.ID}",
                DebugLogger.Category.Handler);
            continue;
          }

          if (!npc.movement.CanGetTo(assignedShelf))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: NPC cannot reach assignedShelf GUID={assignedShelf.GUID} for item={item.ID}",
                DebugLogger.Category.Handler);
            continue;
          }

          if (!StorageExtensions.ShelfCache.TryGetValue(new StorageUtilities.ItemKey(item), out var shelfInfo) ||
              !shelfInfo.ContainsKey(assignedShelf) || !shelfInfo[assignedShelf].IsConfigured)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: ShelfCache missing or assignedShelf GUID={assignedShelf.GUID} not configured for item={item.ID}",
                DebugLogger.Category.Handler);
            continue;
          }

          var deliverySlots = (assignedShelf as ITransitEntity).ReserveInputSlotsForItem(item, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: No delivery slots available for item={item.ID} on assignedShelf GUID={assignedShelf.GUID}",
                DebugLogger.Category.Handler);
            continue;
          }

          var quantity = Mathf.Min(slot.Quantity, (assignedShelf as ITransitEntity).GetInputCapacityForItem(item, npc));
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"FindItemsNeedingMovement: Creating TransferRequest for item={item.ID}, quantity={quantity} from AnyShelf GUID={anyShelf.GUID} to assignedShelf GUID={assignedShelf.GUID}, deliverySlotsCount={deliverySlots.Count}",
              DebugLogger.Category.Handler);

          var request = new TransferRequest(
              pickupLocation: anyShelf,
              pickupItem: item,
              pickupSlot: slot,
              deliveryLocation: assignedShelf,
              deliveryItem: item,
              deliverySlots: deliverySlots,
              quantity: quantity
          );

          if (!pickupGroups.ContainsKey(anyShelf))
            pickupGroups[anyShelf] = new List<TransferRequest>();
          pickupGroups[anyShelf].Add(request);

          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Added TransferRequest to pickupGroups for AnyShelf GUID={anyShelf.GUID}, TotalRequests={pickupGroups[anyShelf].Count}",
              DebugLogger.Category.Handler);
        }
      }

      // Step 3: Check stations (excluding packaging stations)
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Checking stations for property={property.GetType().Name}",
          DebugLogger.Category.Handler);

      if (!PropertyStations.TryGetValue(property, out var stations))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"FindItemsNeedingMovement: No stations found for property={property.GetType().Name}",
            DebugLogger.Category.Handler);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"FindItemsNeedingMovement: Found {stations.Count} stations to process",
            DebugLogger.Category.Handler);

        foreach (var station in stations)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Checking station IsInUse={station.IsInUse}, HasActiveOperation={station.HasActiveOperation}",
              DebugLogger.Category.Handler);

          if (station.IsInUse || station.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Skipping station as it is in use or has active operation",
                DebugLogger.Category.Handler);
            continue;
          }

          if (!npc.movement.CanGetTo(station.GetAccessPoint()))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindItemsNeedingMovement: NPC cannot reach station access point",
                DebugLogger.Category.Handler);
            continue;
          }

          var itemFields = station.GetInputItemForProduct();
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Retrieved {itemFields?.Count ?? 0} itemFields for station",
              DebugLogger.Category.Handler);

          ItemInstance item = null;
          PlaceableStorageEntity shelf = null;
          ItemSlot sourceSlot = null;
          ITransitEntity transitEntity = null;
          List<ItemSlot> deliverySlots = [];
          var found = false;
          int quantity = 0;

          foreach (var itemField in itemFields)
          {
            if (itemField?.SelectedItem == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"FindItemsNeedingMovement: Skipping itemField with null SelectedItem",
                  DebugLogger.Category.Handler);
              continue;
            }

            item = itemField.SelectedItem.GetDefaultInstance();
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Processing item={item.ID} for station",
                DebugLogger.Category.Handler);

            if (station.GetInputQuantity() >= station.StartThreshold)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"FindItemsNeedingMovement: Station input quantity={station.GetInputQuantity()} meets or exceeds StartThreshold={station.StartThreshold} for item={item.ID}",
                  DebugLogger.Category.Handler);
              continue;
            }

            var shelves = StorageUtilities.FindShelvesWithItem(npc, item, needed: 1);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Found {shelves.Count} shelves with item={item.ID}",
                DebugLogger.Category.Handler);

            shelf = shelves.Keys.FirstOrDefault(s => npc.movement.CanGetTo(s) &&
                (s as ITransitEntity).GetFirstSlotContainingTemplateItem(item, ITransitEntity.ESlotType.Output) != null);
            if (shelf == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"FindItemsNeedingMovement: No accessible shelf found with item={item.ID}",
                  DebugLogger.Category.Handler);
              continue;
            }

            sourceSlot = (shelf as ITransitEntity).GetFirstSlotContainingTemplateItem(item, ITransitEntity.ESlotType.Output);
            if (sourceSlot == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"FindItemsNeedingMovement: No source slot found for item={item.ID} on shelf GUID={shelf.GUID}",
                  DebugLogger.Category.Handler);
              continue;
            }

            transitEntity = station as ITransitEntity;
            if (transitEntity == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error,
                  $"FindItemsNeedingMovement: Station is not an ITransitEntity for item={item.ID}",
                  DebugLogger.Category.Handler);
              continue;
            }

            deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"FindItemsNeedingMovement: No delivery slots available for item={item.ID} on station",
                  DebugLogger.Category.Handler);
              continue;
            }

            quantity = Mathf.Min(sourceSlot.Quantity, station.MaxProductQuantity - station.GetInputQuantity());
            if (quantity <= 0)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"FindItemsNeedingMovement: Calculated quantity={quantity} is invalid for item={item.ID} from shelf GUID={shelf.GUID} to station",
                  DebugLogger.Category.Handler);
              continue;
            }

            found = true;
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"FindItemsNeedingMovement: Found valid transfer for item={item.ID}, quantity={quantity} from shelf GUID={shelf.GUID} to station, deliverySlotsCount={deliverySlots.Count}",
                DebugLogger.Category.Handler);
            break; // one per station
          }

          if (found)
          {
            var request = new TransferRequest(
                pickupLocation: shelf,
                pickupItem: item,
                pickupSlot: sourceSlot,
                deliveryLocation: transitEntity,
                deliveryItem: item,
                deliverySlots: deliverySlots,
                quantity: quantity
            );

            if (!pickupGroups.ContainsKey(shelf))
              pickupGroups[shelf] = new List<TransferRequest>();
            pickupGroups[shelf].Add(request);

            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Added TransferRequest to pickupGroups for shelf GUID={shelf.GUID}, TotalRequests={pickupGroups[shelf].Count}",
                DebugLogger.Category.Handler);
          }
        }
      }

      // Step 4: Select up to 5 routes from the same pickup location
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Selecting up to {MAX_ROUTES_PER_CYCLE} routes from {pickupGroups.Count} pickup groups",
          DebugLogger.Category.Handler);

      foreach (var group in pickupGroups.OrderByDescending(g => g.Value.Max(r => GetPriority(r))))
      {
        var groupRequests = group.Value.OrderByDescending(r => GetPriority(r)).Take(MAX_ROUTES_PER_CYCLE).ToList();
        requests.AddRange(groupRequests);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"FindItemsNeedingMovement: Selected {groupRequests.Count} requests from pickup location={group.Key.GetType().Name}",
            DebugLogger.Category.Handler);
        break;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Completed with {requests.Count} transfer requests for NPC={npc.fullName}",
          DebugLogger.Category.Handler);
      return requests;
    }

    private static int GetPriority(TransferRequest request)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetPriority: Calculating priority for TransferRequest with PickupLocation={request.PickupLocation.GetType().Name}, DeliveryLocation={request.DeliveryLocation.GetType().Name}",
          DebugLogger.Category.Handler);

      int priority;
      if (request.DeliveryLocation is IStationAdapter)
      {
        priority = PRIORITY_STATION_REFILL;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"GetPriority: Assigned priority={priority} (PRIORITY_STATION_REFILL) as DeliveryLocation is IStationAdapter",
            DebugLogger.Category.Handler);
      }
      else if (request.PickupLocation is LoadingDock)
      {
        priority = PRIORITY_LOADING_DOCK;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"GetPriority: Assigned priority={priority} (PRIORITY_LOADING_DOCK) as PickupLocation is LoadingDock",
            DebugLogger.Category.Handler);
      }
      else
      {
        priority = PRIORITY_SHELF_RESTOCK;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"GetPriority: Assigned default priority={priority} (PRIORITY_SHELF_RESTOCK)",
            DebugLogger.Category.Handler);
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetPriority: Returning priority={priority} for TransferRequest",
          DebugLogger.Category.Handler);
      return priority;
    }
  }
}