using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ReflectionExecutionProbeFilterTests
	{
		[UnitTest(name: "Scenario action flow: execution probes preserve business stages",
			category: "StaticContract")]
		public static UnitTestResult ProbeCallsDoNotChangeBusinessStages()
		{
			FlowFixture fixture = FlowFixture.Create();
			string plain = LinearMethodFlowValidator.Validate(
				fixture.Contract(fixture.Plain));
			string instrumented = LinearMethodFlowValidator.Validate(
				fixture.Contract(fixture.Instrumented));
			if (plain != null)
				return UnitTestResult.Fail("plain flow is invalid: " + plain);
			return instrumented == null
				? UnitTestResult.Pass("probe call does not alter business stage extraction")
				: UnitTestResult.Fail("instrumented flow changed stages: " + instrumented);
		}

		[UnitTest(name: "Scenario action flow: unrelated Hit remains a business stage",
			category: "StaticContract")]
		public static UnitTestResult UnrelatedHitRemainsVisible()
		{
			FlowFixture fixture = FlowFixture.Create();
			var calls = new[]
			{
				new FlowCallContract(fixture.StageOne, null),
				new FlowCallContract(
					fixture.UnrelatedHit, null, "literal:business", "literal:hit"),
				new FlowCallContract(fixture.StageTwo, null),
			};
			var contract = new LinearMethodFlowContract(
				fixture.Unrelated, Array.Empty<string>(), calls);
			string failure = LinearMethodFlowValidator.Validate(contract);
			return failure == null
				? UnitTestResult.Pass("non-probe Hit remains visible")
				: UnitTestResult.Fail("non-probe Hit was filtered: " + failure);
		}

		private sealed class FlowFixture
		{
			private FlowFixture() { }

			internal MethodInfo Plain { get; private set; }
			internal MethodInfo Instrumented { get; private set; }
			internal MethodInfo Unrelated { get; private set; }
			internal MethodInfo StageOne { get; private set; }
			internal MethodInfo StageTwo { get; private set; }
			internal MethodInfo UnrelatedHit { get; private set; }

			internal LinearMethodFlowContract Contract(MethodInfo method)
				=> new(method, Array.Empty<string>(), new[]
				{
					new FlowCallContract(StageOne, null),
					new FlowCallContract(StageTwo, null),
				});

			internal static FlowFixture Create()
			{
				var assembly = AssemblyBuilder.DefineDynamicAssembly(
					new AssemblyName("ProbeFlowFixture." + Guid.NewGuid().ToString("N")),
					AssemblyBuilderAccess.Run);
				ModuleBuilder module = assembly.DefineDynamicModule("ProbeFlowFixture");
				MethodInfo probe = DefineHit(module, "__SyncExecutionProbe");
				MethodInfo unrelatedHit = DefineHit(module, "BusinessProbe");
				TypeBuilder flow = module.DefineType(
					"ScenarioActionFlow", TypeAttributes.Public | TypeAttributes.Sealed);
				MethodBuilder first = DefineStage(flow, "StageOne");
				MethodBuilder second = DefineStage(flow, "StageTwo");
				DefineFlow(flow, "Plain", new FlowStages(first, second, null));
				DefineFlow(flow, "Instrumented", new FlowStages(first, second, probe));
				DefineFlow(flow, "Unrelated", new FlowStages(first, second, unrelatedHit));
				Type created = flow.CreateType();
				return new FlowFixture
				{
					Plain = Method(created, "Plain"),
					Instrumented = Method(created, "Instrumented"),
					Unrelated = Method(created, "Unrelated"),
					StageOne = Method(created, "StageOne"),
					StageTwo = Method(created, "StageTwo"),
					UnrelatedHit = unrelatedHit,
				};
			}

			private static MethodInfo DefineHit(ModuleBuilder module, string typeName)
			{
				TypeBuilder type = module.DefineType(
					typeName, TypeAttributes.Public | TypeAttributes.Abstract |
					          TypeAttributes.Sealed);
				MethodBuilder hit = type.DefineMethod(
					"Hit", MethodAttributes.Public | MethodAttributes.Static,
					typeof(void), new[] { typeof(string), typeof(string) });
				hit.GetILGenerator().Emit(OpCodes.Ret);
				return type.CreateType().GetMethod("Hit");
			}

			private static MethodBuilder DefineStage(TypeBuilder type, string name)
			{
				MethodBuilder method = type.DefineMethod(
					name, MethodAttributes.Public | MethodAttributes.Static,
					typeof(void), Type.EmptyTypes);
				method.GetILGenerator().Emit(OpCodes.Ret);
				return method;
			}

			private static void DefineFlow(
				TypeBuilder type,
				string name,
				FlowStages stages)
			{
				MethodBuilder method = type.DefineMethod(
					name, MethodAttributes.Public | MethodAttributes.Static,
					typeof(void), Type.EmptyTypes);
				ILGenerator il = method.GetILGenerator();
				il.Emit(OpCodes.Call, stages.First);
				if (stages.Probe != null)
				{
					il.Emit(OpCodes.Ldstr, "business");
					il.Emit(OpCodes.Ldstr, "hit");
					il.Emit(OpCodes.Call, stages.Probe);
				}
				il.Emit(OpCodes.Call, stages.Second);
				il.Emit(OpCodes.Ret);
			}

			private static MethodInfo Method(Type type, string name)
				=> type.GetMethod(name, BindingFlags.Public | BindingFlags.Static);

			private sealed class FlowStages
			{
				internal FlowStages(
					MethodInfo first,
					MethodInfo second,
					MethodInfo probe)
				{
					First = first;
					Second = second;
					Probe = probe;
				}

				internal MethodInfo First { get; }
				internal MethodInfo Second { get; }
				internal MethodInfo Probe { get; }
			}
		}
	}
}
