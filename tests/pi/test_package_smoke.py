import network_hid_bridge


def test_package_exposes_component_identity() -> None:
    assert network_hid_bridge.COMPONENT_NAME == "Raspberry Pi network HID bridge"
