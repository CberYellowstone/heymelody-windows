#include "hm_filament.h"

#include <filament/Camera.h>
#include <filament/Engine.h>
#include <filament/IndirectLight.h>
#include <filament/LightManager.h>
#include <filament/Material.h>
#include <filament/MaterialInstance.h>
#include <filament/Renderer.h>
#include <filament/RenderTarget.h>
#include <filament/Scene.h>
#include <filament/Skybox.h>
#include <filament/SwapChain.h>
#include <filament/Texture.h>
#include <filament/View.h>
#include <filament/Viewport.h>
#include <backend/PixelBufferDescriptor.h>
#include <gltfio/AssetLoader.h>
#include <gltfio/FilamentAsset.h>
#include <gltfio/MaterialProvider.h>
#include <gltfio/ResourceLoader.h>
#include <gltfio/TextureProvider.h>
#include <image/Ktx1Bundle.h>
#include <ktxreader/Ktx1Reader.h>
#include <utils/EntityManager.h>
#include <math/vec3.h>
#include <materials/uberarchive.h>
#include <cgltf.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cctype>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <functional>
#include <future>
#include <iomanip>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using namespace filament;
using namespace filament::gltfio;
using filament::math::float3;
using filament::math::float4;
using Clock = std::chrono::steady_clock;

namespace {
constexpr uint64_t MAX_MODEL_BYTES = 64u * 1024u * 1024u;
constexpr double PI = 3.14159265358979323846;
thread_local std::string creation_error;

uint32_t rotate_right(uint32_t value, unsigned count) {
    return (value >> count) | (value << (32 - count));
}

// SHA-256 is computed over the original complete GLB. The caller cannot grant a profile by name.
std::string sha256(const std::vector<uint8_t>& input) {
    static constexpr uint32_t constants[64] = {
        0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
    };
    uint32_t state[8] = {0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,
        0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
    std::vector<uint8_t> message(input);
    const uint64_t bits = static_cast<uint64_t>(message.size()) * 8;
    message.push_back(0x80);
    while (message.size() % 64 != 56) message.push_back(0);
    for (int i = 7; i >= 0; --i) message.push_back(uint8_t(bits >> (i * 8)));
    for (size_t block = 0; block < message.size(); block += 64) {
        uint32_t words[64]{};
        for (size_t i = 0; i < 16; ++i) {
            const auto p = message.data() + block + i * 4;
            words[i] = uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3];
        }
        for (size_t i = 16; i < 64; ++i) {
            const auto a = words[i - 15], b = words[i - 2];
            words[i] = words[i - 16] + (rotate_right(a,7) ^ rotate_right(a,18) ^ (a >> 3))
                + words[i - 7] + (rotate_right(b,17) ^ rotate_right(b,19) ^ (b >> 10));
        }
        uint32_t a=state[0],b=state[1],c=state[2],d=state[3],e=state[4],f=state[5],g=state[6],h=state[7];
        for (size_t i = 0; i < 64; ++i) {
            const uint32_t t1 = h + (rotate_right(e,6)^rotate_right(e,11)^rotate_right(e,25))
                + ((e & f) ^ (~e & g)) + constants[i] + words[i];
            const uint32_t t2 = (rotate_right(a,2)^rotate_right(a,13)^rotate_right(a,22))
                + ((a & b) ^ (a & c) ^ (b & c));
            h=g;g=f;f=e;e=d+t1;d=c;c=b;b=a;a=t1+t2;
        }
        state[0]+=a;state[1]+=b;state[2]+=c;state[3]+=d;state[4]+=e;state[5]+=f;state[6]+=g;state[7]+=h;
    }
    std::ostringstream result;
    result << std::hex << std::setfill('0');
    for (auto value : state) result << std::setw(8) << value;
    return result.str();
}

bool audited_asset(const std::string& digest) {
    static constexpr const char* hashes[] = {
        "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc",
        "7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6",
        "6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363",
        "b2d5d94fabee06718eaf7378fe6bdbf3db2824db23656a080251df92a04a5060"
    };
    return std::any_of(std::begin(hashes), std::end(hashes), [&](const char* value) { return digest == value; });
}

void validate_environment_ktx(const std::vector<uint8_t>& bytes,size_t maximum) {
    static constexpr uint8_t magic[12]={0xab,0x4b,0x54,0x58,0x20,0x31,0x31,0xbb,0x0d,0x0a,0x1a,0x0a};
    if(bytes.size()<64 || bytes.size()>MAX_MODEL_BYTES || std::memcmp(bytes.data(),magic,12)!=0)
        throw std::runtime_error("Environment must be a bounded KTX1 cubemap");
    const auto u32=[&](size_t offset){uint32_t value;std::memcpy(&value,bytes.data()+offset,4);return value;};
    const auto width=u32(36),height=u32(40),levels=u32(56),metadata=u32(60);
    if(u32(12)!=0x04030201 || !width || width!=height || width>maximum || u32(44) || u32(48) || u32(52)!=6
        || !levels || levels>32 || uint64_t(metadata)>bytes.size()-64)
        throw std::runtime_error("Unsupported or invalid KTX1 cubemap dimensions");
    uint32_t maximum_levels=1;
    for(uint32_t dimension=width;dimension>1;dimension>>=1)++maximum_levels;
    if(levels>maximum_levels)throw std::runtime_error("KTX1 has more mip levels than its dimensions allow");
    size_t offset=64,end=64+metadata;
    while(offset<end) {
        if(end-offset<4)throw std::runtime_error("Truncated KTX1 metadata header");
        const size_t size=u32(offset);offset+=4;
        if(size>end-offset || !size || !std::memchr(bytes.data()+offset,0,size))throw std::runtime_error("Invalid KTX1 metadata item");
        const size_t padded=(size+3)&~size_t(3);
        if(padded>end-offset)throw std::runtime_error("Invalid KTX1 metadata padding");
        offset+=padded;
    }
    for(uint32_t level=0;level<levels;++level) {
        if(bytes.size()-offset<4)throw std::runtime_error("Truncated KTX1 mip header");
        const uint64_t face_size=u32(offset);offset+=4;
        const uint64_t total=((face_size+3)&~uint64_t(3))*6;
        if(!face_size || total>bytes.size()-offset)throw std::runtime_error("Truncated KTX1 cubemap faces");
        offset+=static_cast<size_t>(total);
    }
    if(offset!=bytes.size())throw std::runtime_error("KTX1 payload does not match its declared mip levels");
}

class AuditedMaterials final : public MaterialProvider {
    MaterialProvider* provider_;
    bool audited_;
    bool patched_ = false;
    bool correct(MaterialKey* key, const char* label) {
        const bool selected = audited_ && label && std::strcmp(label, "1001") == 0;
        if (selected) key->alphaMode = AlphaMode::OPAQUE;
        return selected;
    }
public:
    AuditedMaterials(Engine* engine, bool audited) :
        provider_(createUbershaderProvider(engine, UBERARCHIVE_DEFAULT_DATA, UBERARCHIVE_DEFAULT_SIZE)), audited_(audited) {}
    ~AuditedMaterials() override { delete provider_; }
    MaterialInstance* createMaterialInstance(MaterialKey* key, UvMap* uvmap,
            const char* label, const char* extras) override {
        const bool selected = correct(key, label);
        auto* instance = provider_->createMaterialInstance(key, uvmap, label, extras);
        if (selected && instance) {
            instance->setDepthWrite(true);
            if (instance->getMaterial()->getBlendingMode() != BlendingMode::OPAQUE)
                throw std::runtime_error("Audited material did not select an opaque shader");
            patched_ = true;
        }
        return instance;
    }
    Material* getMaterial(MaterialKey* key, UvMap* uvmap, const char* label) override {
        correct(key, label);
        return provider_->getMaterial(key, uvmap, label);
    }
    const Material* const* getMaterials() const noexcept override { return provider_->getMaterials(); }
    size_t getMaterialsCount() const noexcept override { return provider_->getMaterialsCount(); }
    void destroyMaterials() override { provider_->destroyMaterials(); }
    bool needsDummyData(VertexAttribute attribute) const noexcept override { return provider_->needsDummyData(attribute); }
    uint32_t patchedCount() const { return patched_ ? 1u : 0u; }
};

struct Buffer {
    enum class State { FREE, IN_FLIGHT, READY };
    std::vector<uint8_t> pixels;
    State state = State::FREE;
    hm_frame_info info{};
};

class RendererState {
public:
    std::mutex mutex;
    std::condition_variable changed;
    std::deque<std::function<void()>> commands;
    bool stopping = false, render_requested = false, configuration_dirty = false;
    std::thread worker;
    std::promise<int32_t> initialized;
    std::string error;
    hm_configuration configuration{};
    hm_configuration active_configuration{};
    hm_diagnostics diagnostics{};
    std::array<Buffer, 3> buffers;

    Engine* engine = nullptr;
    Renderer* renderer = nullptr;
    SwapChain* swapchain = nullptr;
    Scene* scene = nullptr;
    View* view = nullptr;
    Camera* camera = nullptr;
    Texture* color = nullptr;
    Texture* depth = nullptr;
    RenderTarget* target = nullptr;
    Skybox* skybox = nullptr;
    IndirectLight* indirect = nullptr;
    Texture* ibl_texture = nullptr;
    Texture* sky_texture = nullptr;
    AssetLoader* loader = nullptr;
    FilamentAsset* asset = nullptr;
    std::unique_ptr<AuditedMaterials> materials;
    utils::Entity camera_entity{}, sun_entity{}, fill_entity{};
    float3 center{0,0,0};
    float radius = 1;
    uint64_t sequence = 0, generation = 0;
    uint32_t target_width = 0, target_height = 0, target_msaa = 0;

    explicit RendererState(const hm_create_options& options) {
        configuration.struct_size = sizeof(configuration);
        configuration.width = options.width;
        configuration.height = options.height;
        configuration.msaa_samples = 4;
        configuration.zoom = 1;
        configuration.vertical_fov_degrees = 35;
        configuration.clear_red = configuration.clear_green = configuration.clear_blue = 0.95f;
        configuration.exposure = 1;
        configuration.light_intensity = 100000;
        configuration.fit_margin = 1.08f;
        diagnostics.struct_size = sizeof(diagnostics);
        diagnostics.requested_backend = options.requested_backend;
    }

    ~RendererState() {
        { std::lock_guard lock(mutex); stopping = true; render_requested = false; }
        changed.notify_one();
        if (worker.joinable()) worker.join();
    }

    void fail(const std::string& message) {
        std::lock_guard lock(mutex);
        error = message;
        diagnostics.state = HM_FAILED;
    }

    int32_t invoke(std::function<void()> function) {
        auto promise = std::make_shared<std::promise<int32_t>>();
        auto future = promise->get_future();
        {
            std::lock_guard lock(mutex);
            if (stopping) return HM_NATIVE_ERROR;
            commands.push_back([this, promise, function = std::move(function)] {
                try { function(); promise->set_value(HM_OK); }
                catch (const std::exception& ex) { fail(ex.what()); promise->set_value(HM_NATIVE_ERROR); }
                catch (...) { fail("Unknown native rendering error"); promise->set_value(HM_NATIVE_ERROR); }
            });
        }
        changed.notify_one();
        return future.get();
    }

    void drain() {
        if (engine) { engine->flushAndWait(); engine->pumpMessageQueues(); }
    }

    void initialize() {
        Engine::Backend requested;
        switch (diagnostics.requested_backend) {
            case HM_BACKEND_OPENGL: requested = Engine::Backend::OPENGL; break;
            case HM_BACKEND_METAL: requested = Engine::Backend::METAL; break;
            case HM_BACKEND_VULKAN: requested = Engine::Backend::VULKAN; break;
            default: throw std::runtime_error("Invalid explicitly requested Filament backend");
        }
        engine = Engine::create(requested);
        if (!engine) throw std::runtime_error(diagnostics.requested_backend == HM_BACKEND_VULKAN
            ? "Filament could not initialize Vulkan; a Vulkan 1.1 loader and supported GPU driver are required"
            : "Filament could not initialize the explicitly requested platform GPU backend");
        switch (engine->getBackend()) {
            case Engine::Backend::OPENGL: diagnostics.backend = HM_BACKEND_OPENGL; break;
            case Engine::Backend::METAL: diagnostics.backend = HM_BACKEND_METAL; break;
            case Engine::Backend::VULKAN: diagnostics.backend = HM_BACKEND_VULKAN; break;
            default: throw std::runtime_error("Filament selected an unrecognized backend");
        }
        if (diagnostics.backend != diagnostics.requested_backend)
            throw std::runtime_error("Filament did not initialize the explicitly requested backend");
        renderer = engine->createRenderer();
        Renderer::ClearOptions clear{};
        clear.clearColor = {0,0,0,0};
        clear.clear = true;
        clear.discard = true;
        renderer->setClearOptions(clear);
        scene = engine->createScene();
        view = engine->createView();
        camera_entity = utils::EntityManager::get().create();
        camera = engine->createCamera(camera_entity);
        view->setScene(scene);
        view->setCamera(camera);
        view->setPostProcessingEnabled(true);
        view->setBlendMode(View::BlendMode::TRANSLUCENT);
        view->setDithering(View::Dithering::NONE);
        sun_entity = utils::EntityManager::get().create();
        LightManager::Builder(LightManager::Type::DIRECTIONAL)
            .color({1.0f,0.98f,0.95f}).intensity(100000).direction({-0.4f,-0.7f,-0.5f})
            .castShadows(false).build(*engine, sun_entity);
        scene->addEntity(sun_entity);
        fill_entity = utils::EntityManager::get().create();
        LightManager::Builder(LightManager::Type::DIRECTIONAL)
            .color({0.88f,0.93f,1.0f}).intensity(35000).direction({0.6f,0.1f,0.6f})
            .castShadows(false).build(*engine, fill_entity);
        scene->addEntity(fill_entity);
        float3 sh[9]{};
        sh[0] = {0.5f,0.5f,0.5f};
        indirect = IndirectLight::Builder().irradiance(3, sh).intensity(30000).build(*engine);
        scene->setIndirectLight(indirect);
        skybox = Skybox::Builder().color({0,0,0,0}).build(*engine);
        scene->setSkybox(skybox);
        apply_configuration(configuration);
    }

    void run() noexcept {
        try { initialize(); initialized.set_value(HM_OK); }
        catch (const std::exception& ex) { fail(ex.what()); initialized.set_value(HM_NATIVE_ERROR); cleanup(); return; }
        catch (...) { fail("Filament initialization failed"); initialized.set_value(HM_NATIVE_ERROR); cleanup(); return; }
        for (;;) {
            std::function<void()> command;
            bool render = false, configure = false;
            hm_configuration latest{};
            {
                std::unique_lock lock(mutex);
                const auto ready = [&] { return stopping || !commands.empty() || render_requested || configuration_dirty; };
                const bool in_flight = std::any_of(buffers.begin(), buffers.end(), [](const Buffer& b) { return b.state == Buffer::State::IN_FLIGHT; });
                if (in_flight) changed.wait_for(lock, std::chrono::milliseconds(2), ready);
                else changed.wait(lock, ready);
                if (stopping && commands.empty()) break;
                if (!commands.empty()) { command = std::move(commands.front()); commands.pop_front(); }
                else {
                    configure = configuration_dirty;
                    configuration_dirty = false;
                    latest = configuration;
                    render = render_requested;
                    render_requested = false;
                }
            }
            try {
                engine->pumpMessageQueues();
                if (command) command();
                else {
                    if (configure) apply_configuration(latest);
                    if (render && asset && !render_frame()) {
                        { std::lock_guard lock(mutex);if(!stopping && diagnostics.state==HM_READY)render_requested=true; }
                        std::this_thread::sleep_for(std::chrono::milliseconds(1));
                    }
                    // Buffer-descriptor callbacks also need a flush when the caller requests only one frame.
                    if (!render && !configure) drain();
                }
            } catch (const std::exception& ex) { fail(ex.what()); }
              catch (...) { fail("Filament frame processing failed"); }
        }
        { std::lock_guard lock(mutex); diagnostics.state = HM_STOPPING; }
        cleanup();
    }

    void destroy_targets() {
        if (view) view->setRenderTarget(nullptr);
        if (target) engine->destroy(target);
        if (color) engine->destroy(color);
        if (depth) engine->destroy(depth);
        if (swapchain) engine->destroy(swapchain);
        target=nullptr;color=nullptr;depth=nullptr;swapchain=nullptr;
    }

    void apply_configuration(const hm_configuration& value) {
        const auto maximum=Texture::getMaxTextureSize(*engine,Texture::Sampler::SAMPLER_2D);
        if(value.width>maximum || value.height>maximum)
            throw std::runtime_error("Requested physical dimensions exceed the GPU texture capability");
        if (!target || value.width != target_width || value.height != target_height || value.msaa_samples != target_msaa) {
            drain();
            destroy_targets();
            swapchain = engine->createSwapChain(value.width, value.height);
            color = Texture::Builder().width(value.width).height(value.height).levels(1)
                .format(Texture::InternalFormat::RGBA8)
                .usage(Texture::Usage::COLOR_ATTACHMENT | Texture::Usage::SAMPLEABLE | Texture::Usage::BLIT_SRC).build(*engine);
            depth = Texture::Builder().width(value.width).height(value.height).levels(1)
                .format(Texture::InternalFormat::DEPTH24).usage(Texture::Usage::DEPTH_ATTACHMENT).build(*engine);
            target = RenderTarget::Builder().texture(RenderTarget::AttachmentPoint::COLOR, color)
                .texture(RenderTarget::AttachmentPoint::DEPTH, depth).build(*engine);
            view->setRenderTarget(target);
            view->setViewport({0,0,value.width,value.height});
            View::MultiSampleAntiAliasingOptions msaa{};
            msaa.enabled = value.msaa_samples > 1;
            msaa.sampleCount = static_cast<uint8_t>(value.msaa_samples);
            view->setMultiSampleAntiAliasingOptions(msaa);
            target_width=value.width;target_height=value.height;target_msaa=value.msaa_samples;
            std::lock_guard lock(mutex);
            ++generation;
            for (auto& b : buffers) { b.state=Buffer::State::FREE; b.pixels.resize(size_t(value.width)*value.height*4); }
            diagnostics.width=value.width;diagnostics.height=value.height;diagnostics.msaa_samples=value.msaa_samples;
            diagnostics.generation=generation;
        }
        // The native output is composited over display-sRGB after Filament tone mapping.
        // A transparent constant skybox keeps theme colors out of the HDR lighting pipeline.
        skybox->setColor({0,0,0,0});
        active_configuration=value;
        const double fov = value.vertical_fov_degrees;
        const double aspect = double(value.width)/value.height;
        const double half_angle = std::min(std::atan(std::tan(fov*PI/360.0)*aspect), fov*PI/360.0);
        const double distance = radius / std::sin(half_angle) * value.fit_margin / value.zoom;
        const double yaw=value.yaw_radians, pitch=value.pitch_radians;
        const filament::math::double3 eye = filament::math::double3(center) + filament::math::double3{
            std::sin(yaw)*std::cos(pitch)*distance, std::sin(pitch)*distance, std::cos(yaw)*std::cos(pitch)*distance};
        camera->lookAt(eye, filament::math::double3(center), {0,1,0});
        camera->setProjection(fov, aspect, std::max(0.00001, distance-radius*1.5), distance+radius*4.0, Camera::Fov::VERTICAL);
        // The one-argument Camera helper uses unitless inverse exposure, not the physical
        // exposure coefficient. Keep the documented sunny-day camera and apply our multiplier to ISO.
        camera->setExposure(16.0f,1.0f/125.0f,100.0f*value.exposure);
        auto& lights=engine->getLightManager();
        lights.setIntensity(lights.getInstance(sun_entity),value.light_intensity);
        lights.setIntensity(lights.getInstance(fill_entity),value.light_intensity*0.35f);
    }

    void unload() {
        if (asset) { scene->removeEntities(asset->getEntities(),asset->getEntityCount()); loader->destroyAsset(asset); asset=nullptr; }
        if (loader) AssetLoader::destroy(&loader);
        if (materials) { materials->destroyMaterials(); materials.reset(); }
    }

    void load(std::vector<uint8_t> bytes, std::string expected) {
        if (bytes.size()<20 || bytes.size()>MAX_MODEL_BYTES || std::memcmp(bytes.data(),"glTF",4)!=0)
            throw std::runtime_error("Only bounded self-contained GLB 2.0 models are supported");
        uint32_t version=0,length=0;
        std::memcpy(&version,bytes.data()+4,4);std::memcpy(&length,bytes.data()+8,4);
        if(version!=2 || length!=bytes.size()) throw std::runtime_error("Invalid GLB version or declared length");
        const auto digest=sha256(bytes);
        std::transform(expected.begin(),expected.end(),expected.begin(),[](unsigned char c){return char(std::tolower(c));});
        if(!expected.empty() && expected!=digest) throw std::runtime_error("GLB SHA256 does not match the verified input");
        cgltf_options options{};cgltf_data* parsed=nullptr;
        if(cgltf_parse(&options,bytes.data(),bytes.size(),&parsed)!=cgltf_result_success)
            throw std::runtime_error("GLB metadata could not be parsed");
        std::unique_ptr<cgltf_data,decltype(&cgltf_free)> metadata(parsed,&cgltf_free);
        if(cgltf_validate(parsed)!=cgltf_result_success)
            throw std::runtime_error("GLB accessors or scene metadata are invalid");
        const auto external=[](const char* uri){return uri && *uri && std::strncmp(uri,"data:",5)!=0;};
        for(size_t i=0;i<parsed->buffers_count;++i) if(external(parsed->buffers[i].uri)) throw std::runtime_error("External GLB buffer URI is forbidden");
        for(size_t i=0;i<parsed->images_count;++i) if(external(parsed->images[i].uri)) throw std::runtime_error("External GLB image URI is forbidden");
        uint32_t primitives=0;uint64_t triangles=0;
        for(size_t i=0;i<parsed->meshes_count;++i) for(size_t j=0;j<parsed->meshes[i].primitives_count;++j) {
            const auto& p=parsed->meshes[i].primitives[j];++primitives;
            size_t count=p.indices?p.indices->count:0;
            if(!p.indices) for(size_t k=0;k<p.attributes_count;++k)
                if(p.attributes[k].type==cgltf_attribute_type_position && p.attributes[k].data) count=p.attributes[k].data->count;
            if(p.type==cgltf_primitive_type_triangles) triangles+=count/3;
            else if(p.type==cgltf_primitive_type_triangle_strip || p.type==cgltf_primitive_type_triangle_fan) triangles+=count>2?count-2:0;
        }
        drain();unload();
        { std::lock_guard lock(mutex);diagnostics.state=HM_LOADING;++generation;diagnostics.generation=generation;
          for(auto& b:buffers)b.state=Buffer::State::FREE; }
        materials=std::make_unique<AuditedMaterials>(engine,audited_asset(digest));
        loader=AssetLoader::create({engine,materials.get()});
        asset=loader->createAsset(bytes.data(),static_cast<uint32_t>(bytes.size()));
        if(!asset) throw std::runtime_error("Filament rejected the GLB scene");
        {
            auto decoder=std::unique_ptr<TextureProvider>(createStbProvider(engine));
            {
                ResourceLoader resources({engine,nullptr,true});
                resources.addTextureProvider("image/png",decoder.get());
                resources.addTextureProvider("image/jpeg",decoder.get());
                if(!resources.loadResources(asset)) throw std::runtime_error("Filament could not load complete GLB resources");
                drain();
            }
        }
        const auto bounds=asset->getBoundingBox();
        for(size_t i=0;i<3;++i) if(!std::isfinite(bounds.min[i]) || !std::isfinite(bounds.max[i]) || bounds.min[i]>bounds.max[i])
            throw std::runtime_error("GLB bounds are not finite and ordered");
        center=(bounds.min+bounds.max)*0.5f;
        const auto extent=(bounds.max-bounds.min)*0.5f;
        radius=std::max(0.0001f,std::sqrt(extent.x*extent.x+extent.y*extent.y+extent.z*extent.z));
        scene->addEntities(asset->getEntities(),asset->getEntityCount());
        asset->releaseSourceData();
        hm_configuration current{};
        { std::lock_guard lock(mutex);current=configuration;
          diagnostics.source_meshes=static_cast<uint32_t>(parsed->meshes_count);
          diagnostics.source_nodes=static_cast<uint32_t>(parsed->nodes_count);
          diagnostics.source_primitives=primitives;diagnostics.source_triangles=triangles;
          diagnostics.source_materials=static_cast<uint32_t>(parsed->materials_count);
          diagnostics.patched_materials=materials->patchedCount();
          for(size_t i=0;i<3;++i){diagnostics.bounds_min[i]=bounds.min[i];diagnostics.bounds_max[i]=bounds.max[i];}
          diagnostics.state=HM_READY;error.clear(); }
        apply_configuration(current);
        { std::lock_guard lock(mutex);render_requested=true; }
    }

    void set_environment(std::vector<uint8_t> ibl, std::vector<uint8_t> sky) {
        const auto maximum=Texture::getMaxTextureSize(*engine,Texture::Sampler::SAMPLER_CUBEMAP);
        if(!ibl.empty())validate_environment_ktx(ibl,maximum);
        if(!sky.empty())validate_environment_ktx(sky,maximum);
        drain();
        scene->setIndirectLight(nullptr);
        if(indirect)engine->destroy(indirect);indirect=nullptr;
        if(ibl_texture)engine->destroy(ibl_texture);ibl_texture=nullptr;
        if(sky_texture)engine->destroy(sky_texture);sky_texture=nullptr;
        float3 sh[9]{};sh[0]={0.5f,0.5f,0.5f};
        if(!ibl.empty()) {
            if(ibl.size()<64 || ibl.size()>MAX_MODEL_BYTES)throw std::runtime_error("Invalid bounded IBL KTX1 input");
            auto* bundle=new image::Ktx1Bundle(ibl.data(),static_cast<uint32_t>(ibl.size()));
            bundle->getSphericalHarmonics(sh);
            ibl_texture=ktxreader::Ktx1Reader::createTexture(engine,bundle,false);
        }
        if(!sky.empty()) {
            if(sky.size()<64 || sky.size()>MAX_MODEL_BYTES)throw std::runtime_error("Invalid bounded skybox KTX1 input");
            auto* bundle=new image::Ktx1Bundle(sky.data(),static_cast<uint32_t>(sky.size()));
            sky_texture=ktxreader::Ktx1Reader::createTexture(engine,bundle,false);
        }
        auto builder=IndirectLight::Builder().irradiance(3,sh).intensity(30000);
        if(ibl_texture)builder.reflections(ibl_texture);
        indirect=builder.build(*engine);scene->setIndirectLight(indirect);
        drain();
        // Environment changes are scene revisions too. Never publish the previous lighting as
        // the first completed frame after this synchronous command has returned.
        { std::lock_guard lock(mutex);
          ++generation;diagnostics.generation=generation;
          for(auto& buffer:buffers)buffer.state=Buffer::State::FREE;
          render_requested=true; }
    }

    struct Readback { RendererState* owner;size_t index;hm_frame_info info;Clock::time_point started;std::array<uint8_t,3> background; };
    static void readback_complete(void*,size_t,void* user) {
        std::unique_ptr<Readback> ticket(static_cast<Readback*>(user));
        auto* self=ticket->owner;
        std::lock_guard lock(self->mutex);
        auto& b=self->buffers[ticket->index];
        if(self->stopping || ticket->info.generation!=self->generation) { b.state=Buffer::State::FREE;return; }
        const auto width=ticket->info.width,height=ticket->info.height,stride=ticket->info.stride_bytes;
        // Filament normalizes OpenGL, Vulkan and Metal readPixels to its top-left buffer API.
        (void)stride;
        for(size_t i=0;i<size_t(width)*height*4;i+=4) {
            const uint32_t inverse_alpha=255-b.pixels[i+3];
            for(size_t channel=0;channel<3;++channel)
                b.pixels[i+channel]=static_cast<uint8_t>(std::min<uint32_t>(255,
                    b.pixels[i+channel]+(uint32_t(ticket->background[channel])*inverse_alpha+127)/255));
            b.pixels[i+3]=255;
        }
        b.info=ticket->info;
        b.info.readback_milliseconds=std::chrono::duration<double,std::milli>(Clock::now()-ticket->started).count();
        b.state=Buffer::State::READY;
        self->diagnostics.frame_id=std::max(self->diagnostics.frame_id,b.info.frame_id);
    }

    bool render_frame() {
        size_t index=buffers.size();
        { std::lock_guard lock(mutex);
          if(diagnostics.state!=HM_READY)return true;
          for(size_t i=0;i<buffers.size();++i) if(buffers[i].state==Buffer::State::FREE){index=i;break;}
          if(index==buffers.size()) for(size_t i=0;i<buffers.size();++i)
              if(buffers[i].state==Buffer::State::READY && (index==buffers.size() || buffers[i].info.frame_id<buffers[index].info.frame_id))index=i;
          if(index==buffers.size())return false;
          buffers[index].state=Buffer::State::IN_FLIGHT; }
        const auto started=Clock::now();
        if(!renderer->beginFrame(swapchain)) { std::lock_guard lock(mutex);buffers[index].state=Buffer::State::FREE;return false; }
        renderer->render(view);
        hm_frame_info info{};info.struct_size=sizeof(info);info.width=target_width;info.height=target_height;
        info.stride_bytes=target_width*4;info.frame_id=++sequence;info.generation=generation;
        info.render_milliseconds=std::chrono::duration<double,std::milli>(Clock::now()-started).count();
        const auto to_byte=[](float value){return static_cast<uint8_t>(std::lround(value*255));};
        auto* ticket=new Readback{this,index,info,Clock::now(),{
            to_byte(active_configuration.clear_red),to_byte(active_configuration.clear_green),to_byte(active_configuration.clear_blue)}};
        backend::PixelBufferDescriptor pixels(buffers[index].pixels.data(),buffers[index].pixels.size(),
            backend::PixelDataFormat::RGBA,backend::PixelDataType::UBYTE,1,0,0,target_width,&readback_complete,ticket);
        renderer->readPixels(target,0,0,target_width,target_height,std::move(pixels));
        renderer->endFrame();engine->flush();
        return true;
    }

    void cleanup() noexcept {
        if(!engine)return;
        const auto attempt=[](auto&& action) noexcept {try{action();}catch(...){}};
        // A failed drain must not skip provider teardown: its destructor still needs a live Engine.
        attempt([&]{drain();});
        attempt([&]{if(asset && scene)scene->removeEntities(asset->getEntities(),asset->getEntityCount());});
        attempt([&]{if(asset && loader)loader->destroyAsset(asset);});asset=nullptr;
        attempt([&]{if(loader)AssetLoader::destroy(&loader);});loader=nullptr;
        attempt([&]{if(materials)materials->destroyMaterials();});
        attempt([&]{materials.reset();});
        attempt([&]{if(scene){scene->setSkybox(nullptr);scene->setIndirectLight(nullptr);}});
        attempt([&]{if(skybox)engine->destroy(skybox);});skybox=nullptr;
        attempt([&]{if(indirect)engine->destroy(indirect);});indirect=nullptr;
        attempt([&]{if(ibl_texture)engine->destroy(ibl_texture);});ibl_texture=nullptr;
        attempt([&]{if(sky_texture)engine->destroy(sky_texture);});sky_texture=nullptr;
        attempt([&]{destroy_targets();});
        attempt([&]{if(view)engine->destroy(view);});view=nullptr;
        attempt([&]{if(scene)engine->destroy(scene);});scene=nullptr;
        attempt([&]{if(renderer)engine->destroy(renderer);});renderer=nullptr;
        attempt([&]{if(camera_entity){engine->destroyCameraComponent(camera_entity);utils::EntityManager::get().destroy(camera_entity);}});
        attempt([&]{if(sun_entity){engine->destroy(sun_entity);utils::EntityManager::get().destroy(sun_entity);}});
        attempt([&]{if(fill_entity){engine->destroy(fill_entity);utils::EntityManager::get().destroy(fill_entity);}});
        // Destruction can queue buffer releases. Keep callback owners and buffers alive through this drain.
        attempt([&]{drain();});
        attempt([&]{Engine::destroy(&engine);});engine=nullptr;
    }
};

bool valid_dimensions(uint32_t width,uint32_t height) {
    const uint64_t pixel_capacity=static_cast<uint64_t>(std::numeric_limits<int32_t>::max())/4;
    return width>0 && height>0 && uint64_t(width)<=pixel_capacity/height;
}
}

struct hm_renderer { std::unique_ptr<RendererState> state; };
static_assert(sizeof(hm_configuration)==64);
static_assert(sizeof(hm_frame_info)==48);
static_assert(sizeof(hm_diagnostics)==96);

uint32_t HM_CALL hm_get_abi_version(void) { return HM_FILAMENT_ABI_VERSION; }

int32_t HM_CALL hm_create(const hm_create_options* options,hm_renderer** output) {
    if(!output)return HM_INVALID_ARGUMENT;
    *output=nullptr;creation_error.clear();
    if(!options || options->struct_size!=sizeof(*options))return HM_ABI_MISMATCH;
    if(!valid_dimensions(options->width,options->height))return HM_INVALID_ARGUMENT;
#if defined(__APPLE__)
    if(options->requested_backend!=HM_BACKEND_METAL) {
        creation_error="This runtime only supports the explicitly requested Metal backend";
        return HM_INVALID_ARGUMENT;
    }
#else
    if(options->requested_backend!=HM_BACKEND_OPENGL && options->requested_backend!=HM_BACKEND_VULKAN) {
        creation_error="This runtime requires an explicit Vulkan or OpenGL backend";
        return HM_INVALID_ARGUMENT;
    }
#endif
    try {
        auto handle=std::make_unique<hm_renderer>();
        handle->state=std::make_unique<RendererState>(*options);
        auto* state=handle->state.get();auto ready=state->initialized.get_future();
        state->worker=std::thread([state]{state->run();});
        const auto result=ready.get();
        if(result!=HM_OK){creation_error=state->error;return result;}
        *output=handle.release();return HM_OK;
    }catch(const std::exception& ex){creation_error=ex.what();return HM_NATIVE_ERROR;}
     catch(...){creation_error="Native renderer creation failed";return HM_NATIVE_ERROR;}
}

int32_t HM_CALL hm_load_model(hm_renderer* handle,const void* bytes,uint64_t length,const char* expected) {
    if(!handle || !bytes || length<20 || length>MAX_MODEL_BYTES)return HM_INVALID_ARGUMENT;
    try {
        std::vector<uint8_t> owned(static_cast<const uint8_t*>(bytes),static_cast<const uint8_t*>(bytes)+size_t(length));
        std::string digest=expected?expected:"";
        return handle->state->invoke([state=handle->state.get(),owned=std::move(owned),digest=std::move(digest)]()mutable{state->load(std::move(owned),std::move(digest));});
    }catch(...){return HM_NATIVE_ERROR;}
}

int32_t HM_CALL hm_set_environment(hm_renderer* handle,const void* ibl,uint64_t ilen,const void* sky,uint64_t slen) {
    if(!handle || ilen>MAX_MODEL_BYTES || slen>MAX_MODEL_BYTES || (ilen&&!ibl) || (slen&&!sky))return HM_INVALID_ARGUMENT;
    try {
        std::vector<uint8_t> i,s;
        if(ilen)i.assign(static_cast<const uint8_t*>(ibl),static_cast<const uint8_t*>(ibl)+size_t(ilen));
        if(slen)s.assign(static_cast<const uint8_t*>(sky),static_cast<const uint8_t*>(sky)+size_t(slen));
        return handle->state->invoke([state=handle->state.get(),i=std::move(i),s=std::move(s)]()mutable{state->set_environment(std::move(i),std::move(s));});
    }catch(...){return HM_NATIVE_ERROR;}
}

int32_t HM_CALL hm_configure(hm_renderer* handle,const hm_configuration* configuration) {
    if(!handle || !configuration)return HM_INVALID_ARGUMENT;
    if(configuration->struct_size!=sizeof(*configuration))return HM_ABI_MISMATCH;
    const auto& c=*configuration;
    if(!valid_dimensions(c.width,c.height) || (c.msaa_samples!=1 && c.msaa_samples!=4)
        || !std::isfinite(c.yaw_radians) || !std::isfinite(c.pitch_radians) || std::abs(c.pitch_radians)>=float(PI/2)
        || !std::isfinite(c.zoom) || c.zoom<=0 || !std::isfinite(c.vertical_fov_degrees) || c.vertical_fov_degrees<=0 || c.vertical_fov_degrees>=175
        || !std::isfinite(c.fit_margin) || c.fit_margin<1 || !std::isfinite(c.exposure) || c.exposure<=0
        || !std::isfinite(c.light_intensity) || c.light_intensity<0
        || !std::isfinite(c.clear_red) || !std::isfinite(c.clear_green) || !std::isfinite(c.clear_blue)
        || c.clear_red<0 || c.clear_red>1 || c.clear_green<0 || c.clear_green>1 || c.clear_blue<0 || c.clear_blue>1)return HM_INVALID_ARGUMENT;
    {std::lock_guard lock(handle->state->mutex);if(handle->state->stopping)return HM_NATIVE_ERROR;
     handle->state->configuration=c;handle->state->configuration_dirty=true;}
    handle->state->changed.notify_one();return HM_OK;
}

int32_t HM_CALL hm_render(hm_renderer* handle) {
    if(!handle)return HM_INVALID_ARGUMENT;
    {std::lock_guard lock(handle->state->mutex);if(handle->state->stopping || handle->state->diagnostics.state==HM_FAILED)return HM_NATIVE_ERROR;
     handle->state->render_requested=true;}
    handle->state->changed.notify_one();return HM_OK;
}

int32_t HM_CALL hm_try_copy_latest_frame(hm_renderer* handle,uint64_t after,void* destination,uint64_t capacity,hm_frame_info* output) {
    if(!handle || !output)return HM_INVALID_ARGUMENT;
    if(output->struct_size!=sizeof(*output))return HM_ABI_MISMATCH;
    std::lock_guard lock(handle->state->mutex);
    if(handle->state->diagnostics.state==HM_FAILED)return HM_NATIVE_ERROR;
    const Buffer* newest=nullptr;
    for(const auto& b:handle->state->buffers)if(b.state==Buffer::State::READY && b.info.generation==handle->state->generation
        && b.info.width==handle->state->configuration.width && b.info.height==handle->state->configuration.height
        && b.info.frame_id>after && (!newest || b.info.frame_id>newest->info.frame_id))newest=&b;
    if(!newest)return 0;
    *output=newest->info;
    if(!destination || capacity<newest->pixels.size())return HM_BUFFER_TOO_SMALL;
    std::memcpy(destination,newest->pixels.data(),newest->pixels.size());return 1;
}

int32_t HM_CALL hm_get_diagnostics(hm_renderer* handle,hm_diagnostics* output) {
    if(!handle || !output)return HM_INVALID_ARGUMENT;
    if(output->struct_size!=sizeof(*output))return HM_ABI_MISMATCH;
    std::lock_guard lock(handle->state->mutex);*output=handle->state->diagnostics;return HM_OK;
}

uint32_t HM_CALL hm_get_last_error(hm_renderer* handle,char* destination,uint32_t capacity) {
    std::string error;
    if(handle){std::lock_guard lock(handle->state->mutex);error=handle->state->error;}else error=creation_error;
    if(destination && capacity){const auto n=std::min<size_t>(error.size(),capacity-1);std::memcpy(destination,error.data(),n);destination[n]=0;}
    return static_cast<uint32_t>(error.size()+1);
}

void HM_CALL hm_destroy(hm_renderer* handle) { delete handle; }
