
using Steamworks;

namespace ONI_Together
{
	internal partial class STRINGS
	{
		public partial class UI
		{
			public class MP_SCREEN
			{
				public class MAINMENU
				{
					public static LocString HOSTINGTITLE = "Hosting";
					public class HOSTGAMEBUTTON
					{
						public static LocString TEXT = "Host Game";
					}
					public static LocString JOININGTITLE = "Joining";
					public class JOINVIABUTTONS
					{
						public class STEAM
						{
							public static LocString TEXT = "Steam";
						}
						public class CODE
						{
							public static LocString TEXT = "Code";
						}
						public class LAN
						{
							public static LocString TEXT = "LAN";
						}
					}
					public class STEAMJOIN
					{
						public class JOINVIASTEAM
						{
							public static LocString TEXT = "Join via Steam";
						}
						public class OPENLOBBYLISTBUTTON
						{
							public static LocString TEXT = "Open Lobby Browser";
						}
					}
					public class LOBBYCODEJOIN
					{
						public class INPUT
						{
							public class TEXTAREA
							{
								public static LocString PLACEHOLDER = "Enter Lobby Code...";
								public static LocString TEXT = "​";
							}
						}
						public class JOINWITHCODEBUTTON
						{
							public static LocString TEXT = "Join with Code";
						}
					}
					public class LANJOIN
					{
						public class INPUTS
						{
							public class IPINPUT
							{
								public class TEXTAREA
								{
									public static LocString PLACEHOLDER = "Enter IP address...";
									public static LocString TEXT = "​";
								}
							}
							public class PORT
							{
								public class TEXTAREA
								{
									public static LocString PLACEHOLDER = "Enter Port...";
									public static LocString TEXT = "​";
								}
							}
						}
						public class JOINLANBUTTON
						{
							public static LocString TEXT = "Join LAN address";
						}
					}
					public class CANCEL
					{
						public static LocString TEXT = "Cancel";
					}
				}
				public class HOSTMENU
				{
					public static LocString TITLE = "Host Lobby Settings";
					public class HOSTVIABUTTONS
					{
						public class STEAM
						{
							public static LocString TEXT = "Steam Session";
						}
						public class LAN
						{
							public static LocString TEXT = "LAN Session";
						}
					}
					public class LOBBYSIZE
					{
						public static LocString LABEL = "Lobby Size:";
						public class LOBBYSIZEINPUT
						{
							public class TEXTAREA
							{
								public static LocString PLACEHOLDER = "Enter Lobby Size...";
								public static LocString TEXT = "4​";
							}
						}
					}
					public class STEAMHOSTING
					{
						public class FRIENDSONLY
						{
							public static LocString LABEL = "Private Lobby:";
							public static LocString STATE = "Friends Only";
						}
						public class PASSWORD
						{
							public static LocString PASSWORDTITLE = "Password (optional):";
						}
						public class PASSWORDINPUT
						{
							public class TEXTAREA
							{
								public static LocString PLACEHOLDER = "Leave empty for no password";
								public static LocString TEXT = "​";
							}
						}
					}
					public class LANHOSTING
					{
						public class IPTARGET
						{
							public static LocString LABEL = "Host IP:";
							public class INPUT
							{
								public class TEXTAREA
								{
									public static LocString PLACEHOLDER = "Enter IP address...";
									public static LocString TEXT = "127.0.0.1​";
								}
							}
						}
						public class PORT
						{
							public static LocString LABEL = "Port";
							public class INPUT
							{
								public class TEXTAREA
								{
									public static LocString PLACEHOLDER = "Enter Port...";
									public static LocString TEXT = "8080​";
								}
							}
						}
					}
					public class ADDITIONALSETTINGS
					{
						public static LocString TEXT = "Additional Lobby Settings";
					}
					public class BUTTONS
					{
						public class CANCEL
						{
							public static LocString TEXT = "Cancel";
						}
						public class STARTHOSTING
						{
							public static LocString TEXT = "Start Hosting";
						}
					}
				}
				public class LOBBYLIST
				{
					public static LocString TITLE = "Public Lobby Browser";
					public class SEARCHBAR
					{
						public class INPUT
						{
							public class TEXTAREA
							{
								public static LocString PLACEHOLDER = "Search Lobbies...";
								public static LocString TEXT = "​";
							}
						}
					}
					public class INFO
					{
						public static LocString WORLD = "World Name";
						public static LocString HOST = "Host";
						public static LocString PLAYERS = "Players";
						public static LocString CYCLE = "Cycle";
						public static LocString DUPES = "Dupes";
						public static LocString PING = "Ping";
					}
					public class SCROLLAREA
					{
						public class CONTENT
						{
							public class NOLOBBIES
							{
								public static LocString LABEL = "No public lobbies found. Try hosting your own!";
							}
							public class ENTRYPREFAB
							{
								public class JOINLOBBYBUTTON
								{
									public static LocString TEXT = "Join";
								}
							}
						}
					}
				}
				public class TOPBAR
				{
					public static LocString LABEL = "Multiplayer";
				}
				public class ADDITIONALHOSTSETTINGS
				{
					public static LocString TITLE = "Additional Lobby Settings";
				}
			}

			///Unity UI password input screen, these get automatically applied
			public class MP_PASSWORD_DIALOGUE
			{
				public class HOSTMENU
				{
					public static LocString TITLE = "Password Required!";
					public static LocString PASSWORDTITLE = "Enter password of Lobby:";
					public static LocString PASSWORD_INCORRECT = "Incorrect password!";
					public class PASSWORDINPUT
					{
						public class TEXTAREA
						{
							public static LocString PLACEHOLDER = "Enter lobby password...";
							public static LocString TEXT = "";
						}
					}
					public class BUTTONS
					{
						public class CANCEL
						{
							public static LocString TEXT = "Cancel";
						}
						public class CONFIRM
						{
							public static LocString TEXT = "Confirm";
						}
					}
				}
				public class TOPBAR
				{
					public static LocString LABEL = "Joining password protected lobby";
				}
			}

			///Unity UI for multiplayer lobby state screen

			public class MP_LOBBY_STATE_DIALOGUE
			{
				public class TOPBAR
				{
					public static LocString LABEL = "Multiplayer Session";
				}
				public static LocString LOBBYCODETITLE = "Lobby Code:";
				public class INVITEFRIENDS
				{
					public static LocString TEXT = "Invite Friends";
				}
				public class ENDSESSION
				{
					public static LocString TEXT = "End Session";
				}
			}
		}
	}
}
