# Platform differences

Read when converted code compiles but a platform boundary behaves differently. These are migration-specific areas to inspect, not an exhaustive checklist or additional acceptance gates.

## Window and dispatcher ownership

UWP APIs can imply ownership by the current view, `CoreWindow`, frame, or framework lifetime. WinUI 3 desktop code may require an explicit `Window`, HWND, dispatcher queue, or app-owned lifetime. Carry the actual window and dispatcher through consumers, including asynchronous work and cleanup; do not assume a UWP current-view lookup identifies the desktop owner.

## XAML resources and binding

Resource lookup, dependency-property metadata, binding defaults, generated event signatures, theme resources, and control templates can differ after namespace conversion. Inspect effective runtime values rather than assuming equivalent names preserve them.

A framework control's custom template also depends on its presenters, named parts, resources, and selection assumptions. If the source template relies on incompatible framework internals, retain the target framework's template contract and reapply the source-visible customization.

## Collections and navigation

WinRT and .NET collection boundaries can change notification behavior and selected-item identity. Port collection adapters together with their bindings and selection handling.

Dynamically removing navigation items or closing transient documents can change selection and visual-tree lifetime. Preserve add/select/close/reopen behavior without stale selection or teardown races. Use a target-owned content host when required by target control constraints, not as a routine navigation redesign.

## Package identity and entry points

Packaged and unpackaged desktop apps have different identity and deployment assumptions. Notifications, background work, app services, and secondary windows need their desktop entry paths and lifetime behavior preserved; a successful primary-window launch does not exercise them. For protocol and file associations, read [Activation routing](activation-contracts.md).

## Media, composition, and hosted controls

Media, WebView/editor hosts, composition surfaces, and other native-backed controls combine package dependencies with window, thread, and teardown ownership. Preserve consumed callbacks, content/state synchronization, and disposal order when replacing the host.
