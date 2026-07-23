using System;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultExecutionIlFlowMutationTests
	{
		[UnitTest(name: "Fault runtime IL flow rejects target-context substitution",
			category: "Integration")]
		public static UnitTestResult TargetContextSubstitutionMutationsAreRejected()
		{
			MethodInfo setup = Method("Setup");
			MethodInfo stage = Method("Stage");
			MethodInfo validate = Method("Validate");
			if (Accepts("WrongContext", setup, stage))
				return UnitTestResult.Fail("wrong context argument was accepted");
			if (!Accepts("NestedIdentity", setup, validate))
				return UnitTestResult.Fail("identity alias of setup context was rejected");
			if (Accepts("ReassignedContext", setup, validate))
				return UnitTestResult.Fail("reassigned setup local was accepted");
			if (Accepts("ReplacedByReference", setup, validate))
				return UnitTestResult.Fail("ref-replaced setup local was accepted");
			if (Accepts("ConditionalWrongContext", setup, validate))
				return UnitTestResult.Fail("conditional wrong context was accepted");
			return UnitTestResult.Pass("Operand-stack flow rejects all context substitutions");
		}

		private static bool Accepts(string execution, MethodInfo setup, MethodInfo stage)
			=> FaultExecutionIlFlow.UsesSameSetupLocal(
				Method(execution), setup, new[] { stage });

		private static MethodInfo Method(string name)
			=> typeof(FlowFixture).GetMethod(name,
				BindingFlags.Static | BindingFlags.NonPublic);

		private static class FlowFixture
		{
			internal static FlowContext Setup() => new FlowContext();
			internal static FlowContext Identity(FlowContext value) => value;
			internal static void Stage(FlowContext context, FlowContext input) { }
			internal static void Validate(FlowContext context) { }

			internal static void WrongContext()
			{
				FlowContext expected = Setup();
				FlowContext wrong = new FlowContext();
				Stage(wrong, Identity(expected));
			}

			internal static void NestedIdentity()
			{
				FlowContext expected = Setup();
				Validate(Identity(expected));
			}

			internal static void ReassignedContext()
			{
				FlowContext expected = Setup();
				expected = new FlowContext();
				Validate(expected);
			}

			internal static void ReplacedByReference()
			{
				FlowContext expected = Setup();
				Replace(ref expected);
				Validate(expected);
			}

			internal static void ConditionalWrongContext()
			{
				FlowContext expected = Setup();
				FlowContext wrong = global::System.DateTime.UtcNow.Ticks > 0
					? new FlowContext() : expected;
				Validate(wrong);
			}

			private static void Replace(ref FlowContext value)
				=> value = new FlowContext();
		}

		private sealed class FlowContext { }
	}
}
