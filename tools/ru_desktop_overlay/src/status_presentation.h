#pragma once

#include <cstdint>
#include <string>
#include <string_view>

struct StatusPresentationState {
    std::string last_status;
    bool has_status{false};
    bool last_connected{false};
    bool has_connection{false};
    bool status_after_frame{false};
    std::uint64_t geometry_generation{0};
    std::uint64_t presented_geometry_generation{0};
    std::uint64_t visibility_generation{0};
    std::uint64_t presented_visibility_generation{0};
};

inline bool ShouldPresentStatus(const StatusPresentationState& state,
                                std::string_view status,
                                bool connected,
                                std::uint64_t geometry_generation,
                                std::uint64_t visibility_generation) {
    return !state.has_status || state.last_status != status ||
           !state.has_connection || state.last_connected != connected ||
           state.status_after_frame ||
           state.presented_geometry_generation != geometry_generation ||
           state.presented_visibility_generation != visibility_generation;
}

inline void RecordStatusPresented(StatusPresentationState& state,
                                  std::string_view status,
                                  bool connected,
                                  std::uint64_t geometry_generation,
                                  std::uint64_t visibility_generation) {
    state.last_status.assign(status);
    state.has_status = true;
    state.last_connected = connected;
    state.has_connection = true;
    state.status_after_frame = false;
    state.presented_geometry_generation = geometry_generation;
    state.presented_visibility_generation = visibility_generation;
}

inline void RecordFramePresented(StatusPresentationState& state) {
    state.status_after_frame = true;
}

inline void InvalidateStatusGeometry(StatusPresentationState& state) {
    ++state.geometry_generation;
}

inline void InvalidateStatusVisibility(StatusPresentationState& state) {
    ++state.visibility_generation;
}
