using System;
using System.Collections.Generic;
using System.Text;
using ONI_Together.Networking.Packets.Architecture;

namespace Shared.Interfaces.Networking
{
    /// <summary>
    /// When added to a packet, the host will only broadcast it to clients
    /// whose camera viewport contains the cell returned by <see cref="GetViewportCell"/>.
    /// </summary>
    public interface IViewportCullable : IPacket
    {
        int GetViewportCell();
    }
}
