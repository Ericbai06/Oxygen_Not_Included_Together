using System;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class ScenarioActionMutationShapeValidator
	{
		internal static string ValidatePickup(Type mutation)
		{
			string[] numeric =
			{
				"ItemNetId", "LifecycleRevision", "OriginalStorageNetId",
				"TargetCell",
			};
			foreach (string name in numeric)
			{
				Type type = MemberType(mutation, name);
				if (type != typeof(int) && type != typeof(long)
				    && type != typeof(uint) && type != typeof(ulong))
					return "pickup mutation has invalid identity/lifecycle member " + name;
			}
			return MemberType(mutation, "OriginalPosition")?.FullName == "UnityEngine.Vector3"
				? null : "pickup mutation lacks typed OriginalPosition";
		}

		private static Type MemberType(Type owner, string name)
			=> owner.GetField(name,
				   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType
			   ?? owner.GetProperty(name,
				   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType;
	}
}
