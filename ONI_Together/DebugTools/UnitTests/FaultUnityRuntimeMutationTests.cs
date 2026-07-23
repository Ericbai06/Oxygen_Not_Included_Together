using System;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultUnityRuntimeMutationTests
	{
		[UnitTest(name: "Fault runtime mutation: receipt and clean restore cannot be faked",
			category: "Integration")]
		public static UnitTestResult ReceiptAndCleanRestoreMutationsAreRejected()
		{
			FaultUnityProductionBinding binding = Binding("work.target-missing");
			if (!TryContract(binding, out RuntimeContract contract, out string failure))
				return UnitTestResult.Fail(failure);
			object fault = ValidInput(binding, contract, clean: false);
			if (!Validate(contract, fault)) return UnitTestResult.Fail("valid fault outcome rejected");
			Set(Get(fault, "Receipt"), "Consumed", false);
			if (Validate(contract, fault)) return UnitTestResult.Fail("unconsumed receipt accepted");

			object wrongTarget = ValidInput(binding, contract, clean: false);
			Set(Get(wrongTarget, "Receipt"), "TargetId", "target:other:99");
			if (Validate(contract, wrongTarget)) return UnitTestResult.Fail("wrong-target receipt accepted");
			object receiptOnly = ValidInput(binding, contract, clean: false);
			Set(Get(receiptOnly, "Oracle"), "ObservedTargetId", string.Empty);
			if (Validate(contract, receiptOnly)) return UnitTestResult.Fail("receipt-only oracle accepted");

			object clean = ValidInput(binding, contract, clean: true);
			if (!Validate(contract, clean)) return UnitTestResult.Fail("restored clean-control rejected");
			Set(Get(clean, "Snapshot"), "StateHash", Hash('9'));
			Set(Get(clean, "Oracle"), "AfterHash", Hash('9'));
			return Validate(contract, clean)
				? UnitTestResult.Fail("clean-control accepted without baseline restoration")
				: UnitTestResult.Pass("Receipt, target, oracle and clean restoration mutations are rejected");
		}

		[UnitTest(name: "Fault runtime mutation: destroyed object deltas are rejected",
			category: "Integration")]
		public static UnitTestResult DestroyedObjectMutationsAreRejected()
		{
			FaultUnityProductionBinding binding = Binding("duplicant.destroyed-add-component");
			if (!TryContract(binding, out RuntimeContract contract, out string failure))
				return UnitTestResult.Fail(failure);
			object valid = ValidInput(binding, contract, clean: false);
			if (!Validate(contract, valid))
				return UnitTestResult.Fail("valid destroyed-object outcome rejected");
			(string Property, object Value)[] mutations =
			{
				("ComponentCountAfter", 9),
				("IdentityPresentAfter", true),
				("ExceptionCount", 1),
			};
			foreach ((string property, object value) in mutations)
			{
				object input = ValidInput(binding, contract, clean: false);
				Set(Get(input, "Snapshot"), property, value);
				if (Validate(contract, input))
					return UnitTestResult.Fail("destroyed mutation accepted: " + property);
			}
			return UnitTestResult.Pass("Destroyed object adds no component or identity and throws no exception");
		}

		[UnitTest(name: "Fault runtime mutation: DLC type-only evidence is rejected",
			category: "Integration")]
		public static UnitTestResult DlcTypeOnlyEvidenceIsRejected()
		{
			FaultUnityProductionBinding binding = Binding("dlc.family-aquatic");
			if (!TryContract(binding, out RuntimeContract contract, out string failure))
				return UnitTestResult.Fail(failure);
			object valid = ValidInput(binding, contract, clean: false);
			Set(Get(valid, "Snapshot"), "Evidence", DlcEvidence(null));
			if (!Validate(contract, valid)) return UnitTestResult.Fail("valid DLC evidence rejected");
			foreach (string missing in new[] { "prefab", "identity", "state", "admission" })
			{
				object mutation = ValidInput(binding, contract, clean: false);
				Set(Get(mutation, "Snapshot"), "Evidence", DlcEvidence(missing));
				if (Validate(contract, mutation))
					return UnitTestResult.Fail("DLC evidence accepted without " + missing);
			}
			return UnitTestResult.Pass("DLC oracle independently requires prefab, identity, state and admission");
		}

		private static bool TryContract(
			FaultUnityProductionBinding binding,
			out RuntimeContract contract,
			out string failure)
		{
			MethodInfo setup = Method(binding, "SetupMethod");
			MethodInfo receipt = Method(binding, "ReceiptBarrierMethod");
			MethodInfo snapshot = Method(binding, "SnapshotMethod");
			MethodInfo oracle = Method(binding, "OracleMethod");
			MethodInfo validate = Method(binding, "ValidationMethod");
			if (new[] { setup, receipt, snapshot, oracle, validate }.Any(value => value == null)
			    || validate.ReturnType != typeof(bool) || validate.GetParameters().Length != 1)
			{
				contract = default;
				failure = binding.CaseId + ": typed runtime validator contract is required";
				return false;
			}
			contract = new RuntimeContract
			{
				SetupType = setup.ReturnType,
				ReceiptType = receipt.ReturnType,
				SnapshotType = snapshot.ReturnType,
				OracleType = oracle.ReturnType,
				ValidationMethod = validate,
			};
			failure = null;
			return true;
		}

		private static object ValidInput(
			FaultUnityProductionBinding binding,
			RuntimeContract contract,
			bool clean)
		{
			object setup = New(contract.SetupType);
			Set(setup, "TargetId", "target:fixture:7");
			Set(setup, "BaselineHash", Hash('1'));
			object receipt = New(contract.ReceiptType);
			Set(receipt, "ReceiptId", (clean ? binding.CleanControlReceiptId : binding.ReceiptId));
			Set(receipt, "TargetId", "target:fixture:7");
			Set(receipt, "Consumed", true);
			Set(receipt, "Succeeded", true);
			object snapshot = Snapshot(contract.SnapshotType, clean);
			object oracle = Oracle(contract.OracleType, clean);
			object input = New(contract.ValidationMethod.GetParameters()[0].ParameterType);
			Set(input, "CaseId", binding.CaseId);
			Set(input, "CleanControl", clean);
			Set(input, "Setup", setup);
			Set(input, "Receipt", receipt);
			Set(input, "Snapshot", snapshot);
			Set(input, "Oracle", oracle);
			return input;
		}

		private static object Snapshot(Type type, bool clean)
		{
			object value = New(type);
			Set(value, "TargetId", "target:fixture:7");
			Set(value, "StateHash", Hash(clean ? '1' : '2'));
			Set(value, "InvariantPreserved", true);
			Set(value, "ComponentCountBefore", 8);
			Set(value, "ComponentCountAfter", 8);
			Set(value, "IdentityPresentBefore", false);
			Set(value, "IdentityPresentAfter", false);
			Set(value, "ExceptionCount", 0);
			return value;
		}

		private static object Oracle(Type type, bool clean)
		{
			object value = New(type);
			Set(value, "ObservedTargetId", "target:fixture:7");
			Set(value, "BeforeHash", Hash('1'));
			Set(value, "AfterHash", Hash(clean ? '1' : '2'));
			Set(value, "InvariantPreserved", true);
			Set(value, "Passed", true);
			return value;
		}

		private static TypedEvidenceEnvelope DlcEvidence(string missing)
		{
			var state = new DlcRuntimeState
			{
				StateMachineState = missing == "state" ? string.Empty : "idle",
				AdmissionGeneration = missing == "admission" ? 0 : 4,
			};
			return new TypedEvidenceEnvelope
			{
				SchemaVersion = 1, RunId = "run:fault", DllHash = Hash('a'),
				Scenario = "dlc-runtime", EntryId = "sync:fault:dlc", Role = "host",
				SessionEpoch = 1, ConnectionGeneration = 1, SnapshotGeneration = 1,
				Phase = "fault-oracle", RevisionDomain = "dlc-runtime", Revision = 1,
				Sequence = 1, Target = new DlcRuntimeTarget
				{
					DlcFamily = "Aquatic", Prefab = missing == "prefab" ? string.Empty : "MinnowImperativePOIAConfig",
					Identity = missing == "identity" ? string.Empty : "net:dlc:17",
				}, State = state, StateHash = Hash('b'),
			};
		}

		private static FaultUnityProductionBinding Binding(string id)
			=> FaultUnityBindingRegistry.Bindings.Single(value => value.CaseId == id);

		private static MethodInfo Method(FaultUnityProductionBinding binding, string name)
			=> binding.GetType().GetProperty(name)?.GetValue(binding) as MethodInfo;

		private static object New(Type type) => Activator.CreateInstance(type);

		private static object Get(object owner, string name)
			=> owner.GetType().GetProperty(name)?.GetValue(owner);

		private static void Set(object owner, string name, object value)
		{
			PropertyInfo property = owner?.GetType().GetProperty(name);
			if (property == null || !property.CanWrite)
				throw new InvalidOperationException("Writable runtime property required: " + name);
			property.SetValue(owner, value);
		}

		private static bool Validate(RuntimeContract contract, object input)
			=> (bool)contract.ValidationMethod.Invoke(null, new[] { input });

		private static string Hash(char value) => "sha256:" + new string(value, 64);

		private sealed class RuntimeContract
		{
			internal Type SetupType { get; set; }
			internal Type ReceiptType { get; set; }
			internal Type SnapshotType { get; set; }
			internal Type OracleType { get; set; }
			internal MethodInfo ValidationMethod { get; set; }
		}
	}
}
