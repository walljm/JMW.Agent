-- proj_devices.model: fanned in from whichever raw model field is present for a device
-- (HwSystemModel, DiscoveredModel, BacnetModelName), vendor+OS-dispatched cleanup applied on
-- top (DeviceModelDerivation) — turns a raw SKU string into a clean product-family display name.
-- Kept as its own column rather than overwriting any one of the raw model paths, same fan-in
-- precedent as proj_devices.vendor (DeviceVendorDerivation).
ALTER TABLE proj_devices ADD COLUMN IF NOT EXISTS model TEXT;
