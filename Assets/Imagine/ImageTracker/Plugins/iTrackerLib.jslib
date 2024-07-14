mergeInto(LibraryManager.library, {

	StartWebGLiTracker: function(ids, name)
	{
    	window.iTracker.startTracker(UTF8ToString(ids), UTF8ToString(name));
    },
    StopWebGLiTracker: function()
	{
    	window.iTracker.stopTracker();
    },
    IsWebGLiTrackerReady: function()
    {
        return window.iTracker.FOV != null;
    },
    SetWebGLiTrackerSettings: function(settings)
	{
    	window.iTracker.setTrackerSettings(UTF8ToString(settings), "1.5.3.958141");
    },
    DebugImageTarget: function(id)
    {
        window.iTracker.debugImageTarget(UTF8ToString(id));
    },
    IsWebGLImageTracked: function(id)
    {
        return window.iTracker.isImageTracked(id);
    },
    GetWebGLWarpedTexture: function(id)
    {
        var data = window.iTracker.getWarpedTexture(UTF8ToString(id));
        var bufferSize = lengthBytesUTF8(data) + 1;
        var buffer =  unityInstance.Module._malloc(bufferSize);
        stringToUTF8(data, buffer, bufferSize);
        return buffer;
    },
});
