"""Upload/download speed across physical network adapters."""

import ctypes
import ctypes.wintypes as wt
import time

U64 = ctypes.c_uint64


class GUID(ctypes.Structure):
    _fields_ = [("d1", wt.DWORD), ("d2", wt.WORD), ("d3", wt.WORD), ("d4", ctypes.c_ubyte * 8)]


class MIB_IF_ROW2(ctypes.Structure):
    _fields_ = [
        ("InterfaceLuid", U64), ("InterfaceIndex", wt.ULONG), ("InterfaceGuid", GUID),
        ("Alias", wt.WCHAR * 257), ("Description", wt.WCHAR * 257), ("PhysicalAddressLength", wt.ULONG),
        ("PhysicalAddress", ctypes.c_ubyte * 32), ("PermanentPhysicalAddress", ctypes.c_ubyte * 32),
        ("Mtu", wt.ULONG), ("Type", wt.ULONG), ("TunnelType", ctypes.c_int), ("MediaType", ctypes.c_int),
        ("PhysicalMediumType", ctypes.c_int), ("AccessType", ctypes.c_int), ("DirectionType", ctypes.c_int),
        ("InterfaceAndOperStatusFlags", ctypes.c_ubyte), ("OperStatus", ctypes.c_int), ("AdminStatus", ctypes.c_int),
        ("MediaConnectState", ctypes.c_int), ("NetworkGuid", GUID), ("ConnectionType", ctypes.c_int),
        ("TransmitLinkSpeed", U64), ("ReceiveLinkSpeed", U64),
    ] + [(name, U64) for name in (
        "InOctets InUcastPkts InNUcastPkts InDiscards InErrors InUnknownProtos InUcastOctets InMulticastOctets "
        "InBroadcastOctets OutOctets OutUcastPkts OutNUcastPkts OutDiscards OutErrors OutUcastOctets "
        "OutMulticastOctets OutBroadcastOctets OutQLen"
    ).split()]


class MIB_IF_TABLE2(ctypes.Structure):
    _fields_ = [("NumEntries", wt.ULONG), ("Table", MIB_IF_ROW2 * 1)]


iphlpapi = ctypes.windll.iphlpapi
iphlpapi.GetIfTable2.argtypes = [ctypes.POINTER(ctypes.POINTER(MIB_IF_TABLE2))]
iphlpapi.GetIfEntry2.argtypes = [ctypes.POINTER(MIB_IF_ROW2)]
iphlpapi.FreeMibTable.argtypes = [ctypes.c_void_p]

IF_FLAG_HARDWARE, IF_FLAG_FILTER, IF_TYPE_LOOPBACK = 0x1, 0x2, 24


class NetMeter:
    """Bytes/s across physical adapters (Ethernet, Wi-Fi). Virtual, VPN and filter interfaces are skipped so
    traffic isn't counted twice."""

    def __init__(self):
        self.adapters = []
        self.last = None
        self.find_adapters()

    def find_adapters(self):
        table = ctypes.POINTER(MIB_IF_TABLE2)()
        if iphlpapi.GetIfTable2(ctypes.byref(table)) != 0:
            return
        try:
            n = table.contents.NumEntries
            rows = ctypes.cast(ctypes.byref(table.contents.Table), ctypes.POINTER(MIB_IF_ROW2 * n)).contents
            adapters = [
                r.InterfaceIndex for r in rows
                if r.InterfaceAndOperStatusFlags & IF_FLAG_HARDWARE
                and not r.InterfaceAndOperStatusFlags & IF_FLAG_FILTER
                and r.Type != IF_TYPE_LOOPBACK
            ]
        finally:
            iphlpapi.FreeMibTable(table)
        if adapters != self.adapters:
            self.adapters = adapters
            self.last = None  # totals aren't comparable across a different adapter set

    def sample(self):
        """(up, down) in bytes/s since the previous call, or None on the first call."""
        rx = tx = 0
        for index in self.adapters:
            row = MIB_IF_ROW2(InterfaceIndex=index)
            if iphlpapi.GetIfEntry2(ctypes.byref(row)) == 0:
                rx += row.InOctets
                tx += row.OutOctets
        now = time.perf_counter()
        last, self.last = self.last, (now, rx, tx)
        if last is None or now <= last[0]:
            return None
        elapsed = now - last[0]
        return max(0, tx - last[2]) / elapsed, max(0, rx - last[1]) / elapsed


def fmt_speed(bps):
    if bps >= 1024 * 1024:
        return f"{bps / 1024 / 1024:.2f} MB/s"
    return f"{bps / 1024:.1f} KB/s"
