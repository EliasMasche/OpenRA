#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.GameSaves;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	[SaveableActivity]
	public class DeployForGrantedCondition : Activity
	{
		readonly GrantConditionOnDeploy deploy;
		readonly bool canTurn;
		readonly bool moving;

		public DeployForGrantedCondition(Actor self, GrantConditionOnDeploy deploy, bool moving = false)
		{
			this.deploy = deploy;
			this.moving = moving;
			canTurn = self.Info.HasTraitInfo<IFacingInfo>();
		}

		internal DeployForGrantedCondition(Actor self, SnapshotReader _, MiniYaml yaml)
		{
			canTurn = self.Info.HasTraitInfo<IFacingInfo>();

			var n = yaml.ToDictionary();
			moving = FieldLoader.GetValue<bool>("Moving", n["Moving"].Value);

			var index = FieldLoader.GetValue<int>("Deploy", n["Deploy"].Value);
			var deploys = self.TraitsImplementing<GrantConditionOnDeploy>().ToArray();
			if (index >= 0 && index < deploys.Length)
				deploy = deploys[index];
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			return
			[
				new("Deploy", FieldSaver.FormatValue(self.TraitsImplementing<GrantConditionOnDeploy>().ToList().IndexOf(deploy))),
				new("Moving", FieldSaver.FormatValue(moving))
			];
		}

		protected override void OnFirstRun(Actor self)
		{
			// Turn to the required facing.
			if (deploy.DeployState == DeployState.Undeployed && deploy.Info.Facing.HasValue && canTurn && !moving)
				QueueChild(new Turn(self, deploy.Info.Facing.Value));
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || (deploy.DeployState != DeployState.Deployed && moving))
				return true;

			QueueChild(new DeployInner(deploy));
			return true;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (NextActivity != null)
				foreach (var n in NextActivity.TargetLineNodes(self))
					yield return n;
		}
	}

	[SaveableActivity]
	public class DeployInner : Activity
	{
		const string DeploymentKey = "Deployment";
		const string InitiatedKey = "Initiated";

		readonly GrantConditionOnDeploy deployment;
		bool initiated;

		public DeployInner(GrantConditionOnDeploy deployment)
		{
			this.deployment = deployment;

			// Once deployment animation starts, the animation must finish.
			IsInterruptible = false;
		}

		internal DeployInner(Actor self, SnapshotReader _, MiniYaml yaml)
		{
			IsInterruptible = false;

			var nodes = yaml.ToDictionary();
			initiated = FieldLoader.GetValue<bool>(InitiatedKey, nodes[InitiatedKey].Value);

			var index = FieldLoader.GetValue<int>(DeploymentKey, nodes[DeploymentKey].Value);
			var deployments = self.TraitsImplementing<GrantConditionOnDeploy>().ToArray();
			if (index >= 0 && index < deployments.Length)
				deployment = deployments[index];
		}

		public override List<MiniYamlNode> SaveState(Actor self, SnapshotWriter w)
		{
			var index = self.TraitsImplementing<GrantConditionOnDeploy>().ToList().IndexOf(deployment);
			return
			[
				new(DeploymentKey, FieldSaver.FormatValue(index)),
				new(InitiatedKey, FieldSaver.FormatValue(initiated))
			];
		}

		public override bool Tick(Actor self)
		{
			// Wait for deployment
			if (deployment.DeployState == DeployState.Deploying || deployment.DeployState == DeployState.Undeploying)
				return false;

			if (initiated)
				return true;

			if (deployment.DeployState == DeployState.Undeployed)
				deployment.Deploy();
			else
				deployment.Undeploy();

			initiated = true;
			return false;
		}
	}
}
