%global debug_package %{nil}
%global _build_id_links none
%global __strip /bin/true
%global _rpmformat 4
%global _binary_payload w9.gzdio

Name:           gamepad-filter
Version:        %{app_version}
Release:        1
Summary:        INI-configurable Linux gamepad axis filter
License:        NOASSERTION
URL:            https://github.com/Mashrien/linux-gamepad-transform-driver
Source0:        %{name}-v%{version}-linux-x64.zip
BuildArch:      x86_64
BuildRequires:  unzip
Requires:       glibc >= %{glibc_floor}
Requires:       libudev.so.1()(64bit)

%description
Filter analog gamepad axes in userspace with evdev and uinput.
Use a single INI file for zero offsets, dead zones, clamping, stretch,
and thresholds across supported controllers.

%prep
%setup -q -n %{name}-v%{version}-linux-x64

%install
install -D -m 755 gamepad-filter %{buildroot}%{_bindir}/gamepad-filter

%files
%{_bindir}/gamepad-filter
%doc README.md afterglow.conf
%license DOTNET-LICENSE.txt DOTNET-THIRD-PARTY-NOTICES.txt
