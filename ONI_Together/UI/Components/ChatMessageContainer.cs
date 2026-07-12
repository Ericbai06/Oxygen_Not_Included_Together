using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ONI_Together.UI.Components
{
	internal class ChatMessageContainer : KMonoBehaviour
	{
		[SerializeField]
		LocText Sender, TimeStamp, Text;
		bool init = false;
		string _sender, _stamp, _text;
		bool spawned = false;

		void Init()
		{
			if (init)
				return;
			init = true;
			Sender = transform.Find("Label").gameObject.GetComponent<LocText>();
			Sender.key = string.Empty;
			TimeStamp = transform.Find("TimeStamp").gameObject.GetComponent<LocText>();
			TimeStamp.key = string.Empty;
			Text = transform.Find("Message").gameObject.GetComponent<LocText>();
			Text.key = string.Empty;
		}
		public void SetValues(string sender, string stamp, string text)
		{
			_sender = sender;
			_stamp = stamp;
			_text = text;
			if (spawned)
				ApplyText();
		}
		void ApplyText()
		{
			Init();
			Sender.text = _sender;
			TimeStamp.text = _stamp;
			Text.text = _text;
		}
		public override void OnPrefabInit()
		{
			base.OnPrefabInit();
			Init();
		}
		public override void OnSpawn()
		{
			base.OnSpawn();
			spawned = true;
			ApplyText();
		}

	}
}
