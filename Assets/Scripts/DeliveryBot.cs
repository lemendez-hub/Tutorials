using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class DeliveryBot : MonoBehaviour, IKitchenObjectParent
{
    // States for DelivryBot
    private enum State
    {
        Idle,               // Looking for a plate
        MovingToPlate,      // Walking to a counter with a plate
        MovingToDelivery    // Carrying plate to delivery
    }

    [SerializeField] private Transform holdPoint; // Where held objects are attached
    [SerializeField] private float navMeshSnapDistance = 2f; // Max distance to snap onto NavMesh

    private State state;
    private BaseCounter targetCounter; // Counter currently targeted
    private KitchenObject kitchenObject; // Object currently held
    private NavMeshAgent navMeshAgent;
    private BaseCounter [] counters; // All counters in scene

    private void Awake()
    {
        // Cache NavMeshAgent for movement
        navMeshAgent = GetComponent<NavMeshAgent>();
    }

    private void Start()
    {
        // Find all counters once at start
        counters = FindObjectsOfType<BaseCounter>();
        state = State.Idle;

        // Ensure bot starts on NavMesh
        TrySnapToNavMesh();
    }

    private void Update()
    {
        // Stop movement if game is not active
        if (!KitchenGameManager.Instance.IsGamePlaying())
        {
            SafeResetPath();
            return;
        }

        // If NavMeshAgent is invalid, try fixing position
        if (!CanUseNavMeshAgent())
        {
            TrySnapToNavMesh();
            return;
        }

        // State machine controlling bot behavior
        switch (state)
        {
            case State.Idle:
                SearchForDeliverablePlate();
                break;

            case State.MovingToPlate:
                HandleMoveToPlate();
                break;

            case State.MovingToDelivery:
                HandleMoveToDelivery();
                break;
        }
    }

    // Checks if NavMeshAgent is usable
    private bool CanUseNavMeshAgent()
    {
        return navMeshAgent != null &&
               navMeshAgent.isActiveAndEnabled &&
               navMeshAgent.isOnNavMesh;
    }

    // Attempts to snap bot onto NavMesh if it's off
    private void TrySnapToNavMesh()
    {
        if (navMeshAgent == null || !navMeshAgent.isActiveAndEnabled)
        {
            return;
        }

        if (navMeshAgent.isOnNavMesh)
        {
            return;
        }

        // Find closest valid NavMesh position
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, navMeshSnapDistance, NavMesh.AllAreas))
        {
            navMeshAgent.Warp(hit.position);
        }
    }

    // Safely stops movement
    private void SafeResetPath()
    {
        if (CanUseNavMeshAgent())
        {
            navMeshAgent.ResetPath();
        }
    }

    // Safely sets a movement destination
    private void SafeSetDestination(Vector3 targetPosition)
    {
        if (CanUseNavMeshAgent())
        {
            navMeshAgent.SetDestination(targetPosition);
        }
    }

    // Looks for any plate that matches a waiting recipe
    private void SearchForDeliverablePlate()
    {
        foreach (BaseCounter counter in counters)
        {
            // Skip empty or invalid counters
            if (counter == null || !counter.HasKitchenObject())
            {
                continue;
            }

            KitchenObject counterKitchenObject = counter.GetKitchenObject();

            // Only care about plates
            if (!counterKitchenObject.TryGetPlate(out PlateKitchenObject plateKitchenObject))
            {
                continue;
            }

            // Check if plate matches any recipe
            if (!IsPlateDeliverable(plateKitchenObject))
            {
                continue;
            }

            // Found valid target
            targetCounter = counter;
            SafeSetDestination(targetCounter.transform.position);
            state = State.MovingToPlate;
            return;
        }
    }

    // Handles movement toward a plate
    private void HandleMoveToPlate()
    {
        // If target is invalid, reset
        if (targetCounter == null || !targetCounter.HasKitchenObject())
        {
            ResetBot();
            return;
        }

        SafeSetDestination(targetCounter.transform.position);

        // Wait until path is ready
        if (navMeshAgent.pathPending)
        {
            return;
        }

        // Wait until reached destination
        if (navMeshAgent.remainingDistance > navMeshAgent.stoppingDistance)
        {
            return;
        }

        KitchenObject counterKitchenObject = targetCounter.GetKitchenObject();

        if (counterKitchenObject == null)
        {
            ResetBot();
            return;
        }

        // Ensure it's still a valid plate
        if (!counterKitchenObject.TryGetPlate(out PlateKitchenObject plateKitchenObject))
        {
            ResetBot();
            return;
        }

        if (!IsPlateDeliverable(plateKitchenObject))
        {
            ResetBot();
            return;
        }

        // Pick up plate
        counterKitchenObject.SetKitchenObjectParent(this);

        // Move to delivery counter
        if (DeliveryCounter.Instance != null)
        {
            SafeSetDestination(DeliveryCounter.Instance.transform.position);
            state = State.MovingToDelivery;
        }
        else
        {
            ResetBot();
        }
    }

    // Handles movement toward delivery point
    private void HandleMoveToDelivery()
    {
        if (DeliveryCounter.Instance == null)
        {
            ResetBot();
            return;
        }

        SafeSetDestination(DeliveryCounter.Instance.transform.position);

        if (navMeshAgent.pathPending)
        {
            return;
        }

        if (navMeshAgent.remainingDistance > navMeshAgent.stoppingDistance)
        {
            return;
        }

        // If somehow lost the object, reset
        if (!HasKitchenObject())
        {
            ResetBot();
            return;
        }

        // Deliver plate
        if (GetKitchenObject().TryGetPlate(out PlateKitchenObject plateKitchenObject))
        {
            DeliveryManager.Instance.DeliverRecipe(plateKitchenObject);
            plateKitchenObject.DestroySelf();
        }

        ResetBot();
    }

    // Checks if a plate matches any waiting recipe
    private bool IsPlateDeliverable(PlateKitchenObject plateKitchenObject)
    {
        List<KitchenObjectSO> plateIngredients = plateKitchenObject.GetKitchenObjectSOList();
        List<RecipeSO> waitingRecipes = DeliveryManager.Instance.GetWaitingRecipeSOList();

        foreach (RecipeSO recipe in waitingRecipes)
        {
            // Must match ingredient count
            if (recipe.kitchenObjectSOList.Count != plateIngredients.Count)
            {
                continue;
            }

            bool matches = true;

            // Check all ingredients exist on plate
            foreach (KitchenObjectSO recipeIngredient in recipe.kitchenObjectSOList)
            {
                if (!plateIngredients.Contains(recipeIngredient))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    // Resets bot to idle state
    private void ResetBot()
    {
        targetCounter = null;
        SafeResetPath();
        state = State.Idle;
    }

    // Returns where held object should follow
    public Transform GetKitchenObjectFollowTransform()
    {
        return holdPoint;
    }

    // Assign held object
    public void SetKitchenObject(KitchenObject kitchenObject)
    {
        this.kitchenObject = kitchenObject;
    }

    // Get held object
    public KitchenObject GetKitchenObject()
    {
        return kitchenObject;
    }

    // Clear held object
    public void ClearKitchenObject()
    {
        kitchenObject = null;
    }

    // Check if holding something
    public bool HasKitchenObject()
    {
        return kitchenObject != null;
    }
}