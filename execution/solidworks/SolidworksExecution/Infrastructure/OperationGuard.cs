using System;
using System.Collections.Concurrent;
using System.Threading;
using SolidworksExecution.Models;

namespace SolidworksExecution.Infrastructure
{
    public class OperationGuard : IOperationGuard
    {
        private static readonly OperationGuard _instance = new OperationGuard();
        public static OperationGuard Instance => _instance;

        // The idempotency memory (completed operation ids) and the state_version live in BCL-typed objects parked in the AppDomain's data. In the out-of-process host that changes nothing
        // (one generation, one AppDomain). In the in-process host the tool code is hot-reloaded into the SAME AppDomain, so a new generation picks up the very same memory and version:
        // reloading the tools never invalidates a client's state_version and never forgets which operations already ran.
        private const string SharedSlot = "SolidPilot.OperationGuard";
        private readonly ConcurrentDictionary<string, bool> _completed;
        private readonly int[] _version;

        private OperationGuard()
        {
            var shared = AppDomain.CurrentDomain.GetData(SharedSlot) as object[];
            if (shared == null)
            {
                shared = new object[] { new ConcurrentDictionary<string, bool>(), new int[1] };
                AppDomain.CurrentDomain.SetData(SharedSlot, shared);
            }
            _completed = (ConcurrentDictionary<string, bool>)shared[0];
            _version = (int[])shared[1];
        }

        private int Current => Volatile.Read(ref _version[0]);

        public bool IsDuplicate(string operationId)
        {
            return _completed.ContainsKey(operationId);
        }

        public ExecutionResponse GetDuplicate(string operationId)
        {
            if (_completed.ContainsKey(operationId))
            {
                return new ExecutionResponse
                {
                    OperationId = operationId,
                    Status = "DUPLICATE",
                    LastKnownStateVersion = Current,
                    CadState = null,
                    Error = null
                };
            }
            return null;
        }

        public bool IsStateVersionValid(int incomingStateVersion)
        {
            return incomingStateVersion == Current;
        }

        public void RegisterCompleted(string operationId, ExecutionResponse response)
        {
            _completed[operationId] = true;
            Interlocked.Increment(ref _version[0]);
        }

        public int GetCurrentStateVersion()
        {
            return Current;
        }
    }
}
